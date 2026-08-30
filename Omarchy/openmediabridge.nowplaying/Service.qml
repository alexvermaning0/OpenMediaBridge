import QtQuick
import QtWebSockets

// Both OpenMediaBridge sockets, parsed into plain properties.
//
// Port 8080 carries media info (title/artist/album/cover/state/position) and
// accepts transport commands; port 6555 carries the high-frequency lyric feed
// and accepts the lyric commands (offset, word sync, translation, source).
// Every message is a single `key:value` line, so parsing is a split on the
// first colon — values may contain colons, keys never do.
//
// The shell mounts this once as a `service` plugin, so every bar surface
// shares one pair of connections no matter how many monitors are up;
// BarWidget.qml reaches it with shell.serviceFor("openmediabridge.nowplaying").
// It is also the only file that imports QtWebSockets — without qt6-websockets
// installed the service simply fails to load and the widget says so.
Item {
  id: bridge

  property string host: "127.0.0.1"
  property int mediaPort: 8080
  property int lyricsPort: 6555

  readonly property bool mediaOnline: media.status === WebSocket.Open
  readonly property bool lyricsOnline: lyrics.status === WebSocket.Open
  readonly property bool online: mediaOnline || lyricsOnline

  // Media (port 8080)
  property string title: ""
  property string artist: ""
  property string album: ""
  property string coverUrl: ""
  property string sourceName: ""
  property int durationMs: 0
  property int positionMs: 0
  property bool shuffle: false
  property string repeatMode: "none"

  // 1.x and 2.1+ announce playback state with `status:`; the broken 2.0 never
  // sent it. As a fallback, all versions emit `pos:`/`prog:` while a player is
  // running, so the message flow itself is a (rough) state signal. Prefer an
  // explicit `status:` when the bridge sends one, and fall back to the
  // heartbeat only when it does not.
  property bool explicitPlaying: false
  property bool sawExplicitStatus: false
  property bool tickPlaying: false
  readonly property bool playing: sawExplicitStatus ? explicitPlaying : tickPlaying

  property double lastMessageMs: 0
  property double lastTickMs: 0

  // Lyrics (port 6555)
  property string lyric: ""
  property real progress: 0
  property string lyricSource: ""
  property bool wordSync: false
  property int offsetMs: 0
  property bool translating: false
  property string translateLang: ""

  // 1.x and 2.1+ answer `getstatus` with the full state, so title/artist arrive
  // on connect; the broken 2.0 replied with just the cover URL, leaving them
  // empty until the next track. A live lyric or a moving progress value is
  // therefore also treated as a signal that something is playing.
  readonly property bool hasTrack: title !== "" || artist !== "" || lyric !== "" || progress > 0

  function send(command) {
    if (mediaOnline) media.sendTextMessage(command)
  }

  function sendLyrics(command) {
    if (lyricsOnline) lyrics.sendTextMessage(command)
  }

  function toBool(value) {
    return value === "true" || value === "True" || value === "1"
  }

  function clearMedia() {
    title = ""; artist = ""; album = ""; coverUrl = ""; sourceName = ""
    durationMs = 0; positionMs = 0; shuffle = false
    repeatMode = "none"
    explicitPlaying = false; sawExplicitStatus = false; tickPlaying = false
    lastMessageMs = 0; lastTickMs = 0
  }

  function clearLyrics() {
    lyric = ""; progress = 0; lyricSource = ""
  }

  function handle(message) {
    var split = message.indexOf(":")
    if (split < 0) return
    var key = message.substring(0, split)
    var value = message.substring(split + 1)

    lastMessageMs = Date.now()
    if (key === "pos" || key === "prog") lastTickMs = lastMessageMs

    switch (key) {
    // Media
    case "title":
      // A new track invalidates the line still on screen; the next lyric
      // message may be a second or two out.
      if (value !== title) lyric = ""
      title = value; break
    case "artist": artist = value; break
    case "album": album = value; break
    case "cover": coverUrl = value; break
    case "source": sourceName = value; break
    case "dur": durationMs = parseInt(value) || 0; break
    case "pos": positionMs = parseInt(value) || 0; break
    case "status": explicitPlaying = toBool(value); sawExplicitStatus = true; break
    case "shuffle": shuffle = toBool(value); break
    case "repeat": repeatMode = value; break
    // Lyrics — sent on both sockets, whichever arrives first wins
    case "lyric": lyric = value; break
    case "prog": progress = parseFloat(value) || 0; break
    case "lyricsrc": lyricSource = value; break
    case "wordsync": wordSync = toBool(value); break
    case "offset": offsetMs = parseInt(value) || 0; break
    case "translate": translating = toBool(value); break
    case "translatelang": translateLang = value; break
    }
  }

  WebSocket {
    id: media
    url: "ws://" + bridge.host + ":" + bridge.mediaPort
    active: true
    onTextMessageReceived: function (message) { bridge.handle(message) }
    onStatusChanged: function (status) {
      if (status === WebSocket.Open) bridge.send("getstatus")
      else if (status === WebSocket.Closed || status === WebSocket.Error) {
        bridge.clearMedia()
        mediaRetry.restart()
      }
    }
  }

  WebSocket {
    id: lyrics
    url: "ws://" + bridge.host + ":" + bridge.lyricsPort
    active: true
    onTextMessageReceived: function (message) { bridge.handle(message) }
    onStatusChanged: function (status) {
      if (status === WebSocket.Open) bridge.sendLyrics("status")
      else if (status === WebSocket.Closed || status === WebSocket.Error) {
        bridge.clearLyrics()
        lyricsRetry.restart()
      }
    }
  }

  // Playback state from the heartbeat, plus a stale-state sweep: the bridge
  // stays connected after the last player quits, and nothing would otherwise
  // clear the final lyric out of the bar.
  Timer {
    interval: 1000
    repeat: true
    running: true
    onTriggered: {
      var now = Date.now()
      bridge.tickPlaying = bridge.lastTickMs > 0 && now - bridge.lastTickMs < 3000
      if (bridge.lastMessageMs > 0 && now - bridge.lastMessageMs > 90000) {
        bridge.clearMedia()
        bridge.clearLyrics()
      }
    }
  }

  // OpenMediaBridge is a foreground console app that comes and goes; both
  // sockets retry forever so the widget reappears on its own when it starts.
  Timer {
    id: mediaRetry
    interval: 3000
    onTriggered: { media.active = false; media.active = true }
  }

  Timer {
    id: lyricsRetry
    interval: 3000
    onTriggered: { lyrics.active = false; lyrics.active = true }
  }
}
