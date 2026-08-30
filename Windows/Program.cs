using OpenMediaBridge;
using OpenMediaBridge.Services;
using OpenMediaBridge.Lyrics.Fetchers;
using System.Text.Json;

int port = 8080;
int lyricsPort = 6555;
int coverPort = 8081;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (Environment.OSVersion.Platform == PlatformID.Unix)
{
    Console.Error.WriteLine("This build is the Windows/SMTC port. Use the Linux build on Linux instead.");
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
//   3. otherwise %APPDATA%\OpenMediaBridge.
string ResolveDataDir()
{
    var env = Environment.GetEnvironmentVariable("OPENMEDIABRIDGE_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(env)) return env;

    var localDir = AppContext.BaseDirectory;
    if (File.Exists(Path.Combine(localDir, "config.json"))) return localDir;

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenMediaBridge");
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

// Initialize Resonite WebSocket Server
var server = new ResoniteWSServer("127.0.0.1", configFile.Port)
{
    Config = configFile,
    // Each connected client gets its own session-bound media service via this factory.
    MediaServiceFactory = (session, srv) => new WindowsMediaService(session, srv)
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

// Start Lyrics WebSocket Server (port 6555)
var lyricsServer = new LyricsWSServer("127.0.0.1", configFile.LyricsPort > 0 ? configFile.LyricsPort : lyricsPort, lyricsService);
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
