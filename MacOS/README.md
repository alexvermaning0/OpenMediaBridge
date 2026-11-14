# macOS Build & Run

## Requirements

- macOS 10.15 (Catalina) or newer
- .NET 8.0 SDK installed ([download](https://dotnet.microsoft.com/download/dotnet/8.0))

## Build

### Development Build

```bash
dotnet build MacOS/OpenMediaBridge.MacOS.csproj
```

### Release Build

**Intel (x64)**:
```bash
dotnet publish MacOS/OpenMediaBridge.MacOS.csproj -c Release -r osx-x64 --self-contained
```

**Apple Silicon (ARM64)**:
```bash
dotnet publish MacOS/OpenMediaBridge.MacOS.csproj -c Release -r osx-arm64 --self-contained
```

Output will be in `MacOS/bin/Release/net8.0/osx-{x64|arm64}/publish/`

## Run

From the project root:

```bash
dotnet run --project MacOS/OpenMediaBridge.MacOS.csproj
```

Or run the published executable directly:

```bash
./MacOS/bin/Release/net8.0/osx-x64/publish/OpenMediaBridge
```

## Supported Players

- **Spotify** - Full support
- **Music.app** (formerly iTunes) - Full support
- **iTunes** (legacy) - Full support
- Other players via fallback methods (limited support)

## Permissions

macOS requires allowing Terminal/the application to control media. You may see a prompt asking for permission when running for the first time. Approve it to enable media detection.

## WebSocket Access

Once running, you can access the WebSocket servers:

- **Media Info**: `ws://localhost:8080`
- **Lyrics Only**: `ws://localhost:6555`

Test with a simple client:

```bash
websocat ws://localhost:8080
```

Or in JavaScript:

```javascript
const ws = new WebSocket('ws://localhost:8080');
ws.onmessage = (event) => console.log(event.data);
```

## Configuration

Configuration is stored in `config.json` in the working directory. On first run, a default config will be created automatically.

## Troubleshooting

### "osascript: command not found"

This should not happen on standard macOS installations. Ensure you're running on macOS and that `/usr/bin/osascript` exists:

```bash
which osascript
```

### Media detection not working

1. Check that your player (Spotify, Music.app, etc.) is running
2. Verify the player is actually playing a track
3. Check console output for errors
4. Try running with debug output enabled by modifying the log level in `Program.cs`

### Port already in use

If you see "Port 8080 is already in use", either:
- Stop the other application using that port
- Change the port in `config.json` and restart

### AppleScript timeout

If AppleScript queries are timing out (5 second timeout), this might indicate:
- The player application is frozen
- The player is not responding
- System load is very high

Try restarting the player application.

## Notes

- Media detection runs on a 500ms polling interval
- Changes to `config.json` require a restart
- Lyrics are fetched from LRCLib, NetEase, or local database (in that order)
- Album cover art is fetched from iTunes/Deezer public APIs
