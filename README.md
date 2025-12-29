# OpenMediaBridge

A Windows application that bridges media playback information from Windows Media Session to WebSocket clients. Designed for integration with Resonite VR and other applications.

## Features

- Real-time media info (title, artist, album, cover art, playback state)
- Synchronized lyrics from multiple sources (LRCLib, NetEase, local database)
- Word-by-word sync mode for karaoke-style display
- Public album cover art URLs (iTunes/Deezer APIs)
- Optional Discord status integration
- Dual WebSocket architecture for flexible integration

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
  "discord_show_prefix": true
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

### Commands (Client → Server)

| Command | Short | Description |
|---------|-------|-------------|
| `wordsync:on` | | Enable word sync mode |
| `wordsync:off` | | Disable word sync mode |
| `toggle:wordsync` | `w` | Toggle word sync mode |
| `toggle:offline` | `o` | Toggle offline mode |
| `toggle:cjk` | `c` | Toggle CJK filter |
| `toggle:plain` | `p` | Toggle plain lyrics fallback |
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
| `N` | Cycle to next lyrics source |
| `R` | Re-fetch lyrics |
| `X` | Clear cache for current song |
| `+` | Increase offset by 50ms |
| `-` | Decrease offset by 50ms |
| `Shift++` | Increase offset by 500ms |
| `Shift+-` | Decrease offset by 500ms |
| `S` | Save offset to config |

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

## Building

Requires .NET 8.0 SDK with Windows 10 SDK.

```bash
dotnet build
dotnet run
```

---

## License

MIT
