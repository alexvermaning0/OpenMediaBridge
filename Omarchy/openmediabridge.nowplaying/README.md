# OpenMediaBridge for the Omarchy bar

A Quickshell plugin that puts the OpenMediaBridge feed straight into the
[Omarchy](https://omarchy.org/) status bar on Hyprland: the current lyric line
in the bar, and a popup with the track, cover art, transport, lyric source, and
the offset / word-sync / translation controls the bridge exposes.

It talks to the same two WebSockets as everything else — media on `8080`,
lyrics on `6555` — so it needs no changes to OpenMediaBridge itself.

```
 ⏸  Never gonna give you up            ← the bar
 ┌──────────────────────────────┐
 │ [art]  Never Gonna Give You Up│     ← right click
 │        Rick Astley            │
 │        Whenever You… · spotify│
 │  ▁▁▁▁▁▁▁▁▁▁▂▂▂▂▂▂▂▂           │
 │  1:12                    3:33 │
 │        ⏮   ⏸   ⏭             │
 │  Never gonna give you up      │
 │           lrclib              │
 │ −500 −50  0 ms  +50 +500 Save │
 │ Word sync  Translate  Next src│
 └──────────────────────────────┘
```

## Requirements

- Omarchy with the Quickshell-based shell (`omarchy-shell`)
- The Qt6 WebSockets QML module (`QtWebSockets`). Package name varies by distro:
  - Arch: `sudo pacman -S qt6-websockets`
  - Debian/Ubuntu: `sudo apt install qml6-module-qtwebsockets`
  - Fedora: `sudo dnf install qt6-qtwebsockets`
  - openSUSE: `sudo zypper install qt6-websockets-imports`

  `install.sh` probes for it and prints the right command for your distro if it
  is missing.
- OpenMediaBridge running (the Linux build in `../../Linux`)

## Keeping the bridge running

The widget only has something to show while OpenMediaBridge is up, and the
bridge is a foreground console app that is easy to forget to start. Install it
as a user service so it starts with the desktop and restarts if it dies:

```bash
install -Dm644 ../../Linux/systemd/openmediabridge.service \
  ~/.config/systemd/user/openmediabridge.service
systemctl --user daemon-reload
systemctl --user enable --now openmediabridge.service
```

The unit runs `/usr/bin/openmediabridge` (the AUR package) with
`OPENMEDIABRIDGE_DATA_DIR=~/.config/openmediabridge`; point `ExecStart` at your
own build if you run one. `systemctl --user status openmediabridge` shows
whether it is up.

## Install

```bash
./install.sh
```

That symlinks the plugin into `~/.config/omarchy/plugins/`, rescans, and enables
it. Use `./install.sh --copy` to copy it instead of linking. If your plugins
live somewhere else, point the installer at that folder:

```bash
OMARCHY_PLUGINS_DIR=/path/to/plugins ./install.sh
```

To do it by hand:

```bash
ln -s "$PWD" ~/.config/omarchy/plugins/openmediabridge.nowplaying
omarchy-shell shell rescanPlugins
omarchy plugin enable openmediabridge.nowplaying
```

The widget lands in the center section. Move it with:

```bash
omarchy bar move openmediabridge.nowplaying --section right
```

## Using it

| Action | Result |
|--------|--------|
| Left click | Play / pause |
| Middle click | Next track |
| Right click | Popup with everything else |
| Scroll | Previous / next track (or ±50 ms lyric offset, see `scrollAction`) |
| Hover | Track and artist as a tooltip |

The bar label crossfades between lyric lines instead of scrolling — a marquee
never finishes a line before the next one arrives. Long lines elide at
`maxWidth`; the full line is always in the popup.

When OpenMediaBridge is not running the widget hides itself and keeps retrying
every three seconds, so it comes back on its own once the bridge starts.

## Settings

Settings live on the widget's entry in `~/.config/omarchy/shell.json`, which
hot-reloads on save:

```json
{
  "id": "openmediabridge.nowplaying",
  "host": "127.0.0.1",
  "mediaPort": 8080,
  "lyricsPort": 6555,
  "display": "lyric",
  "maxWidth": 280,
  "showWhenIdle": false,
  "scrollAction": "track"
}
```

| Key | Default | Meaning |
|-----|---------|---------|
| `host` | `127.0.0.1` | Host running OpenMediaBridge — point it at another machine on the LAN if the bridge runs there |
| `mediaPort` | `8080` | Media WebSocket port |
| `lyricsPort` | `6555` | Lyrics WebSocket port |
| `display` | `lyric` | `lyric` (current line, falling back to the track), `track`, or `none` for icon only |
| `maxWidth` | `280` | Pixels before the label elides |
| `showWhenIdle` | `false` | Keep a dimmed icon in the bar when nothing is playing |
| `scrollAction` | `track` | `track` scrolls prev/next, `offset` nudges the lyric offset by 50 ms |

On a vertical bar the label is dropped and only the icon shows, matching the
other Omarchy widgets.

## Hyprland keybindings

The widget registers an IPC target, so the popup and the lyric controls can be
bound to keys. In `~/.config/hypr/bindings.lua`:

```lua
o.bind("SUPER + SHIFT + M", "Lyrics", "omarchy-shell openmediabridge.nowplaying toggle")
o.bind("SUPER + SHIFT + COMMA",  nil, "omarchy-shell openmediabridge.nowplaying offsetDown")
o.bind("SUPER + SHIFT + PERIOD", nil, "omarchy-shell openmediabridge.nowplaying offsetUp")
```

Available calls:

| Call | Effect |
|------|--------|
| `toggle` / `open` / `close` | The popup |
| `playPause`, `next`, `prev` | Transport |
| `offsetUp`, `offsetDown`, `offsetSave` | Lyric offset ±50 ms, and persist it to the bridge's `config.json` |
| `toggleWordSync`, `toggleTranslation` | Word-by-word sync, live translation |
| `nextSource`, `refreshLyrics` | Cycle the lyrics source, re-fetch the current song |

## Version differences

The widget speaks to 1.x, the broken 2.0, and the fixed 2.1+, which do not all
send the same media data on port 8080:

| | 1.x | 2.0 | 2.1+ |
|---|---|---|---|
| `title:` / `artist:` / `album:` on port 8080 | yes | **never sent** | yes |
| `status:` playback state | yes | **never sent** | yes |
| `dur:` track length | yes | **never sent** | yes |
| `getstatus` resends full state | yes | only the cover URL | yes |
| `cover:`, `pos:`, and the whole lyrics socket | yes | yes | yes |

On 1.x and 2.1+ the popup shows the full track, real length, and real play/pause
state. On the broken 2.0 the widget falls back: the bar shows the lyric line by
itself, the popup says "Unknown track" over the cover art, track length is
derived from `pos ÷ prog`, and play/pause state is inferred from the message
heartbeat. Metadata reappears automatically once a version that sends it runs.

> **Updated the bridge but the popup still says "Unknown track"?** A package
> update swaps the files but does not restart a running bridge, so the old
> version keeps serving port 8080. Restart it — `systemctl --user restart
> openmediabridge` if you run the service, otherwise quit and relaunch it — then
> reopen the popup.

## How it fits together

| File | Role |
|------|------|
| `Service.qml` | Both WebSocket connections, parsed into properties. Mounted once by the shell as a `service`, so several monitors still share one pair of sockets. The only file that imports `QtWebSockets`. |
| `BarWidget.qml` | The bar label, the popup, the mouse and IPC surface. |
| `manifest.json` | Plugin declaration and the settings schema. |

## Troubleshooting

**The widget shows a warning icon.** The service did not load — almost always a
missing Qt6 WebSockets QML module (`QtWebSockets`; see Requirements for the
package name on your distro). Check with
`quickshell log -t 100 $(ls -t /run/user/$UID/quickshell/by-id/*/log.qslog | head -1) | grep openmediabridge`.

**Nothing in the bar at all.** Confirm the plugin is enabled
(`omarchy plugin list`) and that OpenMediaBridge is running and something is
playing. Set `showWhenIdle` to `true` to keep the icon visible while idle.

**Edits to `Service.qml` seem to do nothing.** Saving reloads bar widgets, but a
service the shell has already mounted keeps running its old code. Run
`omarchy restart shell` after changing `Service.qml`.

**Lyrics lag the music.** Nudge the offset from the popup and hit Save — that
writes `offset_ms` to the bridge's `config.json`, the same value the desktop UI
uses.
