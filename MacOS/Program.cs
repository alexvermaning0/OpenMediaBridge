using OpenMediaBridge;
using OpenMediaBridge.Services;
using OpenMediaBridge.Lyrics.Fetchers;
using System.Text.Json;
using System.Runtime.InteropServices;

int port = 8080;
int lyricsPort = 6555;
int coverPort = 8081;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    Console.Error.WriteLine("This build is the macOS port. Use the appropriate build for your platform.");
    Environment.Exit(1);
}

// config, cache, db and logs all live in one directory (see ResolveDataDir).
string appDir = ResolveDataDir();
Directory.CreateDirectory(appDir);
OpenMediaBridge.Logging.Log.Init(appDir);

// Everything logs through the one sink; keep the [ERROR]/[WARNING] convention
// working by mapping it onto the logger's levels.
void Log(string message)
{
    if (message.Contains("[ERROR]")) OpenMediaBridge.Logging.Log.Error(message);
    else if (message.Contains("[WARNING]")) OpenMediaBridge.Logging.Log.Warning(message);
    else OpenMediaBridge.Logging.Log.Info(message);
}

// Where the app keeps its files. Precedence:
//   1. OPENMEDIABRIDGE_DATA_DIR when set,
//   2. the folder next to the binary if it already holds a config.json — keeps
//      existing and deliberately "portable" installs working (local files win),
//   3. otherwise ~/Library/Application Support/OpenMediaBridge.
string ResolveDataDir()
{
    var env = Environment.GetEnvironmentVariable("OPENMEDIABRIDGE_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(env)) return env;

    var localDir = AppContext.BaseDirectory;
    if (File.Exists(Path.Combine(localDir, "config.json"))) return localDir;

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, "Library", "Application Support", "OpenMediaBridge");
}

Log("Starting OpenMediaBridge...");
Log($"Data directory: {appDir}");

string configPath = Path.Combine(appDir, "config.json");
string cacheDir   = Path.Combine(appDir, "cache");
string dbPath     = Path.Combine(appDir, "db.sqlite3");

if (!File.Exists(configPath))
{
    Config config = new Config
    {
        Port = port,
        IgnorePlayers = Array.Empty<string>(),
        LyricsPort = lyricsPort,
        CoverPort = coverPort,
        DisableLyricsFor = new List<string>(),
        OffsetMs = 0,
        CacheFolder = cacheDir,
        FilterCjkLyrics = true,
        OfflineMode = false,
        LrclibDatabasePath = dbPath,
        PlainLyricsFallback = false,
        DiscordToken = "",
        DiscordEmoji = "🎶",
        DiscordShowPrefix = true
    };

    JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
    string serializedConfig = JsonSerializer.Serialize(config, options);

    Log($"Config not found - writing new config to {configPath}\n{serializedConfig}");
    File.WriteAllText(configPath, serializedConfig);
}

Config configFile = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath));

LocalDatabaseFetcher.Initialize(configFile.LrclibDatabasePath);

if (configFile.OfflineMode)
{
    Log("[INFO] Offline mode enabled - API calls disabled");
    if (!LocalDatabaseFetcher.IsAvailable())
    {
        Log("[WARNING] Offline mode enabled but local database not found!");
        Log($"[WARNING] Place database file at: {configFile.LrclibDatabasePath}");
    }
}

CoverServer.Start(configFile.CoverPort > 0 ? configFile.CoverPort : coverPort);

var discordService = new DiscordStatusService(configFile);
if (discordService.IsEnabled)
{
    Log("[Discord] Status sync enabled");
}

var server = new ResoniteWSServer("0.0.0.0", configFile.Port)
{
    Config = configFile,
    MediaServiceFactory = (session, srv) => new MacOSMediaService(session, srv)
};

var dummySession = new ResoniteWSSession(server);
var macOSService = new MacOSMediaService(dummySession, server);

var lyricsService = new LyricsService(macOSService);

macOSService.LogCallback = OpenMediaBridge.Logging.Log.Debug;
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
    Log($"Started Media WebSocket Server on port {configFile.Port}");
}
catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
{
    Log($"[ERROR] Could not start Media WebSocket Server: Port {configFile.Port} is already in use.");
    CoverServer.Stop();
    Environment.Exit(1);
}
catch (Exception ex)
{
    Log($"[ERROR] Could not start Media WebSocket Server: {ex.Message}");
    Log($"[ERROR] Stack trace: {ex.StackTrace}");
    CoverServer.Stop();
    Environment.Exit(1);
}

var lyricsServer = new LyricsWSServer("0.0.0.0", configFile.LyricsPort > 0 ? configFile.LyricsPort : lyricsPort, lyricsService);
try
{
    lyricsServer.Start();
    Log($"Started Lyrics WebSocket Server on port {(configFile.LyricsPort > 0 ? configFile.LyricsPort : lyricsPort)}");
}
catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
{
    Log($"[ERROR] Could not start Lyrics WebSocket Server: Port {configFile.LyricsPort} is already in use.");
    server.Stop();
    CoverServer.Stop();
    Environment.Exit(1);
}
catch (Exception ex)
{
    Log($"[ERROR] Could not start Lyrics WebSocket Server: {ex.Message}");
    Log($"[ERROR] Stack trace: {ex.StackTrace}");
    server.Stop();
    CoverServer.Stop();
    Environment.Exit(1);
}

Log("OpenMediaBridge started successfully. Press Q or Escape to stop...");

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
