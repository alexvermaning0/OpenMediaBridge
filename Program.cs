using OpenMediaBridge;
using OpenMediaBridge.Services;
using OpenMediaBridge.Lyrics.Fetchers;
using System.Text.Json;

int port = 8080;
int lyricsPort = 6555;
int coverPort = 8081;

Console.WriteLine("Starting OpenMediaBridge...");
Console.OutputEncoding = System.Text.Encoding.UTF8;


if (Environment.OSVersion.Platform == PlatformID.Unix)
{
    Console.WriteLine("Unfortunately, OpenMediaBridge cannot run under Linux due to Windows-specific libraries being in use.");
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

// Initialize local database if available
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

// Start Cover Server
CoverServer.Start(configFile.CoverPort > 0 ? configFile.CoverPort : coverPort);

// Initialize Discord Status Service (optional - only if token is set)
var discordService = new DiscordStatusService(configFile);
if (discordService.IsEnabled)
{
    Console.WriteLine("[Discord] Status sync enabled");
}

// Initialize Resonite WebSocket Server
var server = new ResoniteWSServer("127.0.0.1", configFile.Port)
{
    Config = configFile
};

// Create shared Windows Media Service instance
var dummySession = new ResoniteWSSession(server);
var wmService = new WindowsMediaService(dummySession, server);

// Create Lyrics Service
var lyricsService = new LyricsService(wmService);

// Connect lyrics service to main session
dummySession.SetLyricsService(lyricsService);

// Connect Discord service to lyrics updates
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

// Start Resonite WebSocket Server (main - port 8080)
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

// Start Lyrics WebSocket Server (port 6555)
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

Console.WriteLine("Press Q or Escape to stop...");
Console.WriteLine();

// Set up quit handler
bool shouldQuit = false;
lyricsService.OnQuitRequested += () => shouldQuit = true;

// Wait for quit signal from LyricsService
while (!shouldQuit)
{
    Thread.Sleep(100);
}

Console.WriteLine();
Console.WriteLine("Stopping...");

// Stop Discord service (clear status)
await discordService.Stop();

server.Stop();
lyricsServer.Stop();
CoverServer.Stop();
lyricsService.Dispose();
wmService.Dispose();
LocalDatabaseFetcher.Cleanup();
Environment.Exit(0);
