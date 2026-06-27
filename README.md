
<img width="100%" height="100%" alt="4c067fc2-4f68-4387-9267-ee5e4349e19c" src="https://github.com/user-attachments/assets/1eed9bff-5a1e-40f3-8a6d-62b23a73bbfa" />

Bridges media playback information from your device's media session to WebSocket clients. Designed for integration with Resonite VR and other applications.

Runs on **Windows** (SMTC), **Linux** (MPRIS/playerctl), and **Android** (standalone, runs its own bridge server on-device). See the `Windows/`, `Linux/`, and `Android/` folders, with shared logic in `Core/`.

## Features

- Real-time media info (title, artist, album, cover art, playback state)
- Synchronized lyrics from multiple sources (LRCLib, NetEase, local database)
- Word-by-word sync mode for karaoke-style display
- Live lyrics translation via any LibreTranslate-compatible API
- Public album cover art URLs (iTunes/Deezer APIs)
- Optional Discord status integration
- Dual WebSocket architecture for flexible integration

## Lyrics UI (Optional)

<img width="3839" height="2160" alt="image" src="https://github.com/user-attachments/assets/7229fad7-a0c7-4bc0-a516-5f623f6596cb" />

> [!TIP]
> **Want a nice lyrics UI?** Download `OBS Templates.zip` from the latest release and open `full.html` in any web browser. You get a full Apple Music-style lyrics viewer with album art, scrolling lyrics, and word sync — no extra software needed.

---

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 8080 | WebSocket | Media info + lyrics |
| 6555 | WebSocket | Lyrics only (dedicated) |

## Configuration

Configuration is stored in `config.json`:

```json
{
  "port": 8080,
  "lyrics_port": 6555,
  "ignorePlayers": [],
  "offset_ms": 0,
  "cache_folder": "cache",
  "filter_cjk_lyrics": true,
  "offline_mode": false,
  "lrclib_database_path": "db.sqlite3",
  "plain_lyrics_fallback": false,
  "discord_token": "",
  "discord_emoji": "🎶",
  "discord_show_prefix": true,
  "translation_enabled": false,
  "translation_target_lang": "en",
  "translation_libretranslate_url": "https://translate.minekingshosting.nl",
  "translation_api_key": ""
}
```

| Option | Description |
|--------|-------------|
| `port` | Main WebSocket port for media info |
| `lyrics_port` | Dedicated lyrics WebSocket port |
| `ignorePlayers` | Array of player names to ignore |
| `offset_ms` | Global lyrics timing offset in milliseconds |
| `cache_folder` | Folder for cached lyrics files |
| `filter_cjk_lyrics` | Skip lyrics that are mostly CJK characters |
| `offline_mode` | Only use local sources, no API calls |
| `lrclib_database_path` | Path to LRCLib SQLite database for offline use |
| `plain_lyrics_fallback` | Use plain (unsynced) lyrics if no synced available |
| `discord_token` | Discord user token for status sync (leave empty to disable) |
| `discord_emoji` | Emoji shown in Discord status |
| `discord_show_prefix` | Add prefix to Discord status text |
| `translation_enabled` | Translate lyrics to `translation_target_lang` on startup |
| `translation_target_lang` | Target language code (e.g. `en`, `nl`, `ja`) |
| `translation_libretranslate_url` | LibreTranslate-compatible API endpoint |
| `translation_api_key` | API key for the LibreTranslate instance, if required |

---

## WebSocket Protocol

All messages use a simple `key:value` format. Each message is sent separately (not combined).

---

## Port 8080 - Media WebSocket

### Messages Sent (Server → Client)

#### Media Info (sent on connect and song change)

| Message | Description | Example |
|---------|-------------|---------|
| `title:<text>` | Song title | `title:Never Gonna Give You Up` |
| `artist:<text>` | Artist name | `artist:Rick Astley` |
| `album:<text>` | Album name | `album:Whenever You Need Somebody` |
| `dur:<ms>` | Duration in milliseconds | `dur:213000` |
| `source:<name>` | Media player source | `source:Spotify` |
| `cover:<url>` | Album cover URL (public) | `cover:https://is1-ssl.mzstatic.com/...` |

#### Playback State (sent on connect and state change)

| Message | Description | Example |
|---------|-------------|---------|
| `status:<bool>` | Playing (true) or paused (false) | `status:true` |
| `shuffle:<bool>` | Shuffle enabled | `shuffle:false` |
| `repeat:<mode>` | Repeat mode (none/track/list) | `repeat:none` |
| `pos:<ms>` | Current position in ms (every 1 second) | `pos:45000` |

#### Lyrics Info (sent on connect and change)

| Message | Description | Example |
|---------|-------------|---------|
| `lyric:<text>` | Current lyric line | `lyric:Never gonna give you up` |
| `prog:<0-1>` | Song progress (every 1 second) | `prog:0.472` |
| `lyricsrc:<source>` | Lyrics source | `lyricsrc:lrclib` |
| `wordsync:<bool>` | Word sync mode enabled | `wordsync:false` |
| `offset:<ms>` | Current offset in ms | `offset:-50` |
| `translate:<bool>` | Translation enabled | `translate:true` |
| `translatelang:<code>` | Translation target language | `translatelang:en` |

### Commands (Client → Server)

#### Media Controls

| Command | Short | Description |
|---------|-------|-------------|
| `play` | | Resume playback |
| `pause` | | Pause playback |
| `next` | | Skip to next track |
| `prev` | | Previous track |
| `previous` | | Previous track |
| `stop` | | Stop playback |

#### Lyrics Controls

| Command | Short | Description |
|---------|-------|-------------|
| `toggle:wordsync` | `w` | Toggle word-by-word sync mode |
| `toggle:offline` | `o` | Toggle offline mode |
| `toggle:cjk` | `c` | Toggle CJK lyrics filter |
| `toggle:plain` | `p` | Toggle plain lyrics fallback |
| `toggle:translation` | `t` | Toggle lyrics translation |
| `lang:<code>` | | Set translation target language (e.g. `lang:nl`) |
| `nextlyrics` | `n` | Cycle to next lyrics source |
| `refresh` | `r` | Re-fetch lyrics for current song |
| `clearcache` | `x` | Clear cache for current song |

#### Offset Controls

| Command | Short | Description |
|---------|-------|-------------|
| `offset:+50` | `+` | Increase offset by 50ms |
| `offset:-50` | `-` | Decrease offset by 50ms |
| `offset:+500` | | Increase offset by 500ms |
| `offset:-500` | | Decrease offset by 500ms |
| `offset:save` | `s` | Save current offset to config |

#### Info Commands

| Command | Short | Description |
|---------|-------|-------------|
| `getstatus` | `?` | Resend all current state |
| `status` | `?` | Resend all current state |
| `getfulllyrics` | | Get full lyrics text (newline separated) |
| `help` | `h` | List available commands |

---

## Port 6555 - Lyrics WebSocket

Dedicated connection for lyrics display. Receives high-frequency lyric updates.

### Messages Sent (Server → Client)

| Message | Description | Example |
|---------|-------------|---------|
| `lyric:<text>` | Current lyric line | `lyric:Never gonna let you down` |
| `prog:<0-1>` | Song progress | `prog:0.523` |
| `wordsync:<bool>` | Word sync mode | `wordsync:true` |
| `lyricsrc:<source>` | Lyrics source | `lyricsrc:lrclib` |
| `offset:<ms>` | Current offset | `offset:0` |
| `translate:<bool>` | Translation enabled | `translate:true` |
| `translatelang:<code>` | Translation target language | `translatelang:en` |

### Commands (Client → Server)

| Command | Short | Description |
|---------|-------|-------------|
| `wordsync:on` | | Enable word sync mode |
| `wordsync:off` | | Disable word sync mode |
| `toggle:wordsync` | `w` | Toggle word sync mode |
| `toggle:offline` | `o` | Toggle offline mode |
| `toggle:cjk` | `c` | Toggle CJK filter |
| `toggle:plain` | `p` | Toggle plain lyrics fallback |
| `toggle:translation` | `t` | Toggle lyrics translation |
| `lang:<code>` | | Set translation target language (e.g. `lang:nl`) |
| `next` | `n` | Cycle to next lyrics source |
| `refresh` | `r` | Re-fetch lyrics |
| `clearcache` | `x` | Clear cache for current song |
| `offset:+50` | `+` | Increase offset by 50ms |
| `offset:-50` | `-` | Decrease offset by 50ms |
| `offset:+500` | | Increase offset by 500ms |
| `offset:-500` | | Decrease offset by 500ms |
| `offset:save` | `s` | Save offset to config |
| `status` | `?` | Resend current lyrics state |
| `getfulllyrics` | | Get full lyrics text |
| `help` | `h` | List commands |

---

## Keyboard Shortcuts (Console)

| Key | Description |
|-----|-------------|
| `Q` / `Esc` | Quit application |
| `H` | Toggle help display |
| `W` | Toggle word sync mode |
| `O` | Toggle offline mode |
| `C` | Toggle CJK filter |
| `P` | Toggle plain lyrics fallback |
| `T` | Toggle lyrics translation |
| `L` | Open language picker (then `Esc` to close) |
| `N` | Cycle to next lyrics source |
| `R` | Re-fetch lyrics |
| `X` | Clear cache for current song |
| `+` | Increase offset by 50ms |
| `-` | Decrease offset by 50ms |
| `Shift++` | Increase offset by 500ms |
| `Shift+-` | Decrease offset by 500ms |
| `S` | Save offset to config |

### Language Picker (`L`)

| Key | Language | Key | Language |
|-----|----------|-----|----------|
| `E` | English | `6` | Japanese |
| `1` | Arabic | `7` | Korean |
| `2` | Chinese | `8` | Portuguese |
| `D` | Dutch | `9` | Russian |
| `3` | French | `0` | Spanish |
| `4` | German | | |
| `5` | Italian | | |

---

## Lyrics Sources

Lyrics are fetched in this priority order:

1. **Cache** - Previously fetched and cached lyrics
2. **Local Database** - SQLite database (for offline use)
3. **LRCLib** - Online synced lyrics database
4. **NetEase** - Chinese music service (fallback)

### Lyrics Source Values

| Source | Description |
|--------|-------------|
| `cache` | Loaded from cache |
| `localdb` | From local SQLite database |
| `lrclib` | From LRCLib API |
| `netease` | From NetEase API |
| `lrclib (plain)` | Plain lyrics with estimated timing |
| `None` | No lyrics found |

---

## Cover Art

Album covers are fetched from public APIs (no API key required):

1. **iTunes Search API** - Primary source
2. **Deezer API** - Fallback

If no cover is found, a default image is used.

---

## Discord Integration

Optional feature to show current lyrics in Discord custom status.

### Setup

1. Get your Discord token:
   - Open Discord in browser (discord.com/app)
   - Press F12 → Network tab
   - Refresh page, filter by "api"
   - Find "authorization" header in any request
2. Add token to `config.json`:
   ```json
   {
     "discord_token": "your_token_here"
   }
   ```

### Behavior

- Updates status with current lyric line
- Clears status when no lyrics or playback stopped
- Rate limited to 1 update per second
- Status auto-expires after 5 minutes
- Strips color tags from word sync mode

---

## Android App

The Android app (`Android/`) is a standalone bridge, not a client — it runs its own embedded WebSocket server on-device, reading now-playing info via Android's notification listener / media session APIs instead of SMTC or MPRIS. Useful for offloading the bridge from a PC entirely (e.g. running OMB straight from your phone).

It implements the same `key:value` protocol on the same default ports, including lyrics caching, offline mode, the CJK filter, plain-lyrics fallback, and lyrics translation with a target-language picker. The on-screen translate button mirrors the console's color cues: dim when off, yellow while fetching, green once translated text is ready.

Build with Android Studio or `cd Android && ./gradlew assembleDebug`. Requires notification access permission to read media sessions.

---

## Resonite Integration

### Parsing Messages

Messages use `key:value` format. In ProtoFlux:

1. Use `String Contains` to check for prefix (e.g., "title:")
2. Use `String Replace` to remove prefix and get value
3. Or use `String Split` with ":" delimiter

### Example Flow

```
Connect to ws://localhost:8080
↓
Receive initial state:
  title:Song Name
  artist:Artist
  status:true
  ...
↓
Receive updates:
  lyric:Current line
  prog:0.523
  pos:112000
```

---

## OBS Templates

<img width="3839" height="2160" alt="image" src="https://github.com/user-attachments/assets/7229fad7-a0c7-4bc0-a516-5f623f6596cb" />

*`full.html` showing the Apple Music-style immersive view with album art, scrolling full-song lyrics, and blurred background. Alongside it, the OMB console showing live lyric sync.*

Browser-source overlays for OBS Studio included in the `OBS Templates` folder (also available as a zip in each release).

> [!TIP]
> **Want a lyrics UI without OBS?**
> `full.html` works as a standalone lyrics viewer — just open it in any web browser while OpenMediaBridge is running. You get the full Apple Music-style experience with album art, scrolling lyrics, word sync, and progress bar, no streaming software needed.

### Overlays

| File | Description | Size |
|------|-------------|------|
| `mini.html` | Compact corner widget — art, title, artist, current lyric, progress bar | 420 × 110 px |
| `full.html` | Apple Music-style immersive view — art on left, full song lyrics on right, blurred art background | 1920 × 1080 px |
| `transparent.html` | Same as full but transparent background, anchored to top for OBS cropping | 1920 × 1080 px |
| `lyrics-only.html` | Scrolling lyrics only, transparent background, progress bar at bottom | 1920 × 1080 px |

### Setup in OBS

1. Add a **Browser Source** to your scene
2. Check **Local file** and browse to the `.html` file
3. Set the width/height as listed above
4. Check **Shutdown source when not visible** (optional, saves resources)
5. Make sure OpenMediaBridge is running on the same machine

If you changed the ports in `config.json`, update `WS_MEDIA_URL` and `WS_LYRICS_URL` at the top of the `<script>` block in each file.

### Cropping `transparent.html` in OBS

The panel is 260px tall. To hide the empty canvas below it:
1. Right-click the source → **Filters**
2. Add **Crop/Pad**
3. Set **Bottom** to `820` (for 1080p)

### Word Sync

All overlays support word-by-word highlighting. The current word highlights in **amber** within the active line. Toggle with the `w` key or `toggle:wordsync` command.

### Customization

CSS variables at the top of each file's `<style>` block:

| Variable | Description |
|----------|-------------|
| `--active-opacity` | Opacity of the current lyric line |
| `--idle-opacity` | Opacity of surrounding lines |
| `--sung-opacity` | Opacity of past lines |
| `--ts` | Transition speed |
| `--panel-height` | Panel height (`transparent.html` only) |
| `--art-size` | Album art size (`transparent.html` only) |


## Building

**Windows** — requires .NET 8.0 SDK with Windows 10 SDK:
```bash
dotnet build Windows/OpenMediaBridge.Windows.csproj
dotnet run --project Windows/OpenMediaBridge.Windows.csproj
```

**Linux** — requires .NET 8.0 SDK and `playerctl`:
```bash
dotnet build Linux/OpenMediaBridge.Linux.csproj
dotnet run --project Linux/OpenMediaBridge.Linux.csproj
```

**Android** — open the `Android/` folder in Android Studio, or build from the command line:
```bash
cd Android
./gradlew assembleDebug
```

CI builds all three platforms automatically — see `.github/workflows/build.yml`.

---
