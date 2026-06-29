using OpenMediaBridge;
using OpenMediaBridge.Services;
using OpenMediaBridge.Lyrics.Fetchers;
using System.Text.Json;
using System.Runtime.InteropServices;

int port = 8080;
int lyricsPort = 6555;
int coverPort = 8081;

Console.WriteLine("Starting OpenMediaBridge...");
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    Console.WriteLine("This build is the macOS port. Use the appropriate build for your platform.");
    Environment.Exit(1);
}

if (!File.Exists("config.json"))
{
    Config config = new Config
    {
        Port = port,
        IgnorePlayers = Array.Empty<string>(),
        LyricsPort = lyricsPort,
        CoverPort = coverPort,
        DisableLyricsFor = new List<string>(),
        OffsetMs = 0,
        CacheFolder = "cache",
        FilterCjkLyrics = true,
        OfflineMode = false,
        LrclibDatabasePath = "db.sqlite3",
        PlainLyricsFallback = false,
        DiscordToken = "",
        DiscordEmoji = "🎶",
        DiscordShowPrefix = true
    };

    JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
    string serializedConfig = JsonSerializer.Serialize(config, options);

    Console.WriteLine($"Config not found - writing new config\n{serializedConfig}");
    File.WriteAllText("config.json", serializedConfig);
}

Config configFile = JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json"));

LocalDatabaseFetcher.Initialize(configFile.LrclibDatabasePath);

if (configFile.OfflineMode)
{
    Console.WriteLine("[INFO] Offline mode enabled - API calls disabled");
    if (!LocalDatabaseFetcher.IsAvailable())
    {
        Console.WriteLine("[WARNING] Offline mode enabled but local database not found!");
        Console.WriteLine($"[WARNING] Place database file at: {configFile.LrclibDatabasePath}");
    }
}

CoverServer.Start(configFile.CoverPort > 0 ? configFile.CoverPort : coverPort);

var discordService = new DiscordStatusService(configFile);
if (discordService.IsEnabled)
{
    Console.WriteLine("[Discord] Status sync enabled");
}

var server = new ResoniteWSServer("127.0.0.1", configFile.Port)
{
    Config = configFile,
    MediaServiceFactory = (session, srv) => new MacOSMediaService(session, srv)
};

var dummySession = new ResoniteWSSession(server);
var macOSService = new MacOSMediaService(dummySession, server);

var lyricsService = new LyricsService(macOSService);

macOSService.Start();

dummySession.SetLyricsService(lyricsService);

if (discordService.IsEnabled)
{
    lyricsService.OnLyricUpdate += (lyric, progress) =>
    {
        if (lyric != null)
        {
            _ = discordService.UpdateLyric(lyric);
        }
    };
}

try
{
    server.Start();
    Console.WriteLine($"Started Media WebSocket Server on port {configFile.Port}");
}
catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
{
    Console.WriteLine($"[ERROR] Could not start Media WebSocket Server: Port {configFile.Port} is already in use.");
    CoverServer.Stop();
    Environment.Exit(1);
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] Could not start Media WebSocket Server: {ex.Message}");
    Console.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
    CoverServer.Stop();
    Environment.Exit(1);
}

var lyricsServer = new LyricsWSServer("127.0.0.1", configFile.LyricsPort > 0 ? configFile.LyricsPort : lyricsPort, lyricsService);
try
{
    lyricsServer.Start();
    Console.WriteLine($"Started Lyrics WebSocket Server on port {(configFile.LyricsPort > 0 ? configFile.LyricsPort : lyricsPort)}");
}
catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
{
    Console.WriteLine($"[ERROR] Could not start Lyrics WebSocket Server: Port {configFile.LyricsPort} is already in use.");
    server.Stop();
    CoverServer.Stop();
    Environment.Exit(1);
}
catch (Exception ex)
{
    Console.WriteLine($"[ERROR] Could not start Lyrics WebSocket Server: {ex.Message}");
    Console.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
    server.Stop();
    CoverServer.Stop();
    Environment.Exit(1);
}

Console.WriteLine("Press Q or Escape to stop...");
Console.WriteLine();

bool shouldQuit = false;
lyricsService.OnQuitRequested += () => shouldQuit = true;

while (!shouldQuit)
{
    Thread.Sleep(100);
}

Console.WriteLine();
Console.WriteLine("Stopping...");

await discordService.Stop();

server.Stop();
lyricsServer.Stop();
CoverServer.Stop();
lyricsService.Dispose();
macOSService.Dispose();
LocalDatabaseFetcher.Cleanup();
Environment.Exit(0);
