# OpenMediaBridge — Android Edition

A native Android port of [OpenMediaBridge](https://github.com/...) that runs entirely on-device — no PC required. Reads media sessions from Spotify, YouTube Music, and any other Android media player, fetches synced lyrics, and broadcasts everything via WebSocket using the same OMB protocol.

---

## Architecture

```
Android Media Session API
        │
        ▼
MediaListenerService          ← NotificationListenerService
(reads what's playing)           reads all active MediaSessions
        │
        ▼
BridgeService (Foreground)    ← ties everything together
        ├── LyricsFetcher     ← cache → LRCLib → NetEase
        ├── OMBWebSocketServer:8080   ← media info + commands
        └── OMBWebSocketServer:6555   ← lyrics stream
        │
        ▼
MainActivity                  ← full.html-style lyrics UI
```

---

## Prerequisites

- Android Studio Hedgehog (2023.1.1) or newer
- Android SDK 34
- Min SDK 26 (Android 8.0 Oreo)
- Device or emulator running Android 8.0+

---

## Building

```bash
# Clone and open in Android Studio, or:
./gradlew assembleDebug
# APK output: app/build/outputs/apk/debug/app-debug.apk
```

---

## First Launch — Permission Setup

OpenMediaBridge requires **Notification Access** to read media sessions. This is the Android equivalent of Windows' GSMTC API.

1. Launch the app → it opens the Setup screen
2. Tap **"Open Notification Access Settings"**
3. Find **OpenMediaBridge** in the list and enable it
4. Return to the app → tap **Continue**

> This permission is required once. Android will ask you to confirm since it grants broad notification access. OpenMediaBridge only reads media session data — it does not read or process any notification content.

---

## WebSocket Protocol

Identical to the Windows version. Connect from Resonite or any other client:

| Port | Purpose |
|------|---------|
| 8080 | Media info + full protocol |
| 6555 | Lyrics stream (dedicated) |

Your device's IP is shown at the top of the app screen.

### Messages sent (same as OMB Windows)

```
title:Never Gonna Give You Up
artist:Rick Astley
album:Whenever You Need Somebody
source:Spotify
dur:213000
status:true
pos:45000
cover:https://is1-ssl.mzstatic.com/...
lyric:Never gonna give you up
prog:0.472
lyricsrc:lrclib
wordsync:false
fulllyrics:<newline separated lines>
```

### Commands accepted

```
play / pause / next / prev / stop
toggle:wordsync  (or w)
toggle:offline   (or o)
toggle:cjk       (or c)
toggle:plain     (or p)
nextlyrics       (or n)
refresh          (or r)
clearcache       (or x)
offset:+50       (or +)
offset:-50       (or -)
offset:+500
offset:-500
getfulllyrics
getstatus
```

---

## Lyrics Sources

Same priority order as Windows:

1. **Cache** — file-based JSON cache in app's private storage
2. **LRCLib API** — `https://lrclib.net` (synced LRC lyrics)
3. **NetEase** — Chinese music API (fallback)
4. **Plain lyrics** — estimated timing (if `plain_lyrics_fallback` enabled)

---

## Supported Media Players

Any app that implements Android's `MediaSession` API will work:

- ✅ Spotify
- ✅ YouTube Music
- ✅ YouTube
- ✅ Apple Music (Android)
- ✅ Tidal
- ✅ Amazon Music
- ✅ Deezer
- ✅ SoundCloud
- ✅ Podcast Addict, Pocket Casts
- ✅ Most music/podcast apps

---

## Key Files

| File | Purpose |
|------|---------|
| `MediaListenerService.kt` | NotificationListenerService — reads Android MediaSessions |
| `BridgeService.kt` | Foreground service — orchestrates everything |
| `LyricsFetcher.kt` | Lyrics engine (mirrors `LyricsFetcher.cs`) |
| `WebSocketServer.kt` | NanoHTTPD-based WS server (mirrors `LyricsWSServer.cs`) |
| `MainActivity.kt` | Lyrics UI (mirrors `full.html` aesthetics) |
| `LyricsAdapter.kt` | RecyclerView adapter with active/sung/upcoming states |

---

## Differences from Windows Version

| Feature | Windows | Android |
|---------|---------|---------|
| Media API | GSMTC (Windows.Media.Control) | MediaSession + NotificationListenerService |
| Permission | None needed | Notification Access (one-time) |
| Cover art | iTunes / Deezer APIs | Same |
| Local DB | SQLite `.db` file | Not yet (add path in config) |
| Discord status | Optional | Not yet implemented |
| CJK filter | ✅ | ✅ |
| Word sync | ✅ | ✅ |
| Multiple sources | ✅ | ✅ |

---

## Connecting Resonite

In your Resonite world, connect to:
```
ws://<your-phone-ip>:8080
```
Parse messages the same way as the PC version — the protocol is identical.

---

## Battery & Background Behaviour

The app uses:
- `PARTIAL_WAKE_LOCK` — keeps CPU alive for position tracking
- `WIFI_MODE_FULL_HIGH_PERF` wifi lock — maintains network for WebSocket clients
- `START_STICKY` service — Android restarts it if killed

On some aggressive battery-saver devices (Xiaomi, Samsung with aggressive task-killer), you may need to:
- Add OpenMediaBridge to "Don't kill" / "Battery optimization excluded" in system settings
- Enable "Auto-start" in device settings

---

## Adding Local Database (Optional)

The Windows version supports an LRCLib SQLite database for offline use.
Android support: copy your `db.sqlite3` to the device and set `lrclib_database_path` in config.
*(LocalDatabaseFetcher Kotlin port is a TODO — the index building and SQLite queries are straightforward to port from `LocalDatabaseFetcher.cs`)*
