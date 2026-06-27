# OpenMediaBridge — CLAUDE.md

This file is context for Claude Code when working on this project.

## What This Project Is

**OpenMediaBridge (OMB)** is a Windows-only C# bridge application that:
- Reads the **currently playing song** from Windows SMTC (System Media Transport Controls)
- Fetches **synchronized lyrics** from multiple sources
- Serves everything over **dual WebSocket APIs**
- Outputs to **OBS overlays** (HTML templates), **Resonite VR**, **Discord status**, and any WebSocket client

Primary consumers: Resonite VR (port 8080), OBS overlays (HTML templates connecting to both ports), Discord status integration.

---

## Project Structure

```
OMB/
├── Program.cs                      Entry point, service wiring, startup/shutdown
├── Config.cs                       JSON config model (config.json)
├── ResoniteWSServer.cs             WebSocket server (port 8080)
├── ResoniteWSSession.cs            Main WS session: media + lyrics + commands
├── Services/
│   ├── WindowsMediaService.cs      SMTC listener (event-driven media metadata)
│   ├── Lyrics_Service.cs           Core loop: lyric sync, keyboard input, events
│   ├── Lyrics_WSServer.cs          Lyrics-only WebSocket server (port 6555)
│   ├── Lyrics_WSSession.cs         Lyrics-only WS session handler
│   ├── DiscordStatusService.cs     Optional Discord status sync
│   └── CoverServer.cs              Album art fetcher (iTunes + Deezer APIs)
├── Lyrics/
│   ├── LyricsFetcher.cs            Orchestrates 4 sources, manages cache
│   ├── CacheHelper.cs              Disk cache (.lrc files in cache/ folder)
│   └── fetchers/
│       ├── LRCLibFetcher.cs        lrclib.net (primary online synced lyrics)
│       ├── NetEaseFetcher.cs       music.163.com (Chinese music fallback)
│       ├── LocalDatabaseFetcher.cs SQLite offline source
│       └── DatabaseIndex.cs        SQLite index helper
└── OBS Templates/
    ├── full.html                   Apple Music-style 1920×1080 overlay
    ├── transparent.html            Same but transparent background
    ├── mini.html                   420×110 compact corner widget
    └── lyrics-only.html            Scrolling lyrics, transparent background
```

---

## Tech Stack

- **Language:** C# / .NET 8.0 — Windows only (targets Windows 10.0.22000.0)
- **WebSocket server:** NetCoreServer 8.0.7
- **Database:** Microsoft.Data.Sqlite 9.0.0
- **Serialization:** System.Text.Json 9.0.4
- **Media:** Windows.Media.Control (SMTC, built-in)
- **Frontend:** Vanilla HTML5/CSS3/JS (no frameworks) in OBS Templates/

### External APIs (no keys required)
- lrclib.net — synced lyrics
- music.163.com — Chinese music lyrics
- iTunes Search API — album art (primary)
- Deezer API — album art (fallback)
- Discord API — custom status (requires user token in config)

---

## WebSocket Protocol

All messages use `key:value` format (plain text, no JSON).

**Port 8080 — Full media + lyrics**

| Direction | Messages |
|-----------|----------|
| Server → Client | `title:`, `artist:`, `album:`, `dur:`, `source:`, `cover:`, `status:`, `shuffle:`, `repeat:`, `pos:`, `lyric:`, `prog:`, `lyricsrc:`, `wordsync:`, `offset:` |
| Client → Server | `play`, `pause`, `next`, `prev`, `toggle:wordsync`, `offset:+50`, `offset:-50`, `refresh`, source cycling commands |

**Port 6555 — Lyrics-only (high frequency)**
- Lighter protocol: `lyric:`, `prog:`, same offset/source commands

---

## Configuration (config.json)

Auto-generated on first run. Key fields:

| Field | Default | Notes |
|-------|---------|-------|
| `port` | 8080 | Main WebSocket port |
| `lyrics_port` | 6555 | Lyrics-only WebSocket port |
| `cover_port` | 8081 | Cover art server port |
| `offset_ms` | 0 | Global lyric sync offset (ms) |
| `filter_cjk_lyrics` | true | Skip CJK lyrics (Chinese/Japanese/Korean) |
| `offline_mode` | false | Only use cache + local DB |
| `plain_lyrics_fallback` | false | Use unsynced lyrics if synced unavailable |
| `discord_token` | `""` | Leave empty to disable Discord integration |
| `ignorePlayers` | `[]` | SMTC player names to ignore |

---

## Lyrics Source Priority

1. Disk cache (`cache/` folder — `.lrc` files)
2. Local SQLite database (`db.sqlite3`)
3. LRCLib API (`lrclib.net`)
4. NetEase API (`music.163.com`)

---

## Build & Run

```bash
dotnet build
dotnet run
```

Requires: .NET 8.0 SDK + Windows 10 SDK (Windows 11 recommended)

---

## Key Behaviors & Rules

### Platform
- **Windows only.** Uses `Windows.Media.Control` (SMTC). Do not attempt to add cross-platform support without explicit discussion.

### WebSocket Protocol
- The `key:value` format is intentional — keep it. Resonite and existing OBS templates parse this exact format. Do not switch to JSON on WebSocket messages without updating all consumers.

### OBS Templates
- Templates are standalone HTML files. They must remain self-contained (no build step, no npm). Vanilla JS only. They connect to both WS ports and must handle reconnection gracefully.

### Lyrics Sync
- The sync loop runs at ~100ms ticks. Be careful with anything that adds blocking delay inside `Lyrics_Service.cs`.
- Offset calibration is user-driven (keyboard `+`/`-`). Don't change offset behavior without understanding the save/apply flow in `Config.cs`.

### Discord Integration
- Uses a **user token** (not a bot token). Rate limit: 1 update/second. This is already enforced in `DiscordStatusService.cs` — don't remove the throttle.

### CJK Filter
- NetEase often returns Chinese lyrics. The `filter_cjk_lyrics` config flag is the user's opt-in to skip these. Respect it in any new lyrics source.

### Caching
- Always cache fetched lyrics to disk after a successful fetch. `CacheHelper.cs` handles this. New fetchers should integrate with it.

---

## Keyboard Shortcuts (Console UI)

| Key | Action |
|-----|--------|
| H | Show help |
| W | Toggle word-sync mode |
| O | Toggle offline mode |
| C | Cycle lyrics source |
| P | Play/Pause |
| N | Next track |
| R | Previous track |
| X | Clear Discord status |
| +/- | Adjust sync offset (+50ms / -50ms) |
| Q / Esc | Quit |

---

## What to Watch Out For

- **`Lyrics_Service.cs`** is the largest file and the heart of the runtime. It handles the tick loop, keyboard, events, and console UI simultaneously. Read it carefully before modifying.
- **SMTC debouncing** is in `WindowsMediaService.cs` — rapid song changes are intentionally debounced. Don't remove this.
- **`ResoniteWSSession.cs`** sends the full state on new client connect, then deltas. If adding new state, update both paths.
- **The HTML templates** use CSS variables and connect to both WS ports. Test changes against all four templates.
