import QtQuick
import Quickshell
import Quickshell.Io
import qs.Ui
import qs.Commons

// OpenMediaBridge in the bar: the current lyric line while something is
// playing, with the track behind it in a popup that also carries the transport,
// the lyric source, and the offset/word-sync/translation controls the bridge
// exposes over its lyrics socket.
BarWidget {
  id: root
  moduleName: "openmediabridge.nowplaying"

  // One service instance backs every bar surface, so two monitors still mean
  // one pair of sockets. It is null until the shell mounts it, and stays null
  // when it cannot load at all (missing qt6-websockets).
  readonly property var bridge: bar && bar.shell ? bar.shell.serviceFor("openmediabridge.nowplaying") : null
  property bool bridgeMissing: false
  readonly property bool online: bridge ? bridge.online : false
  readonly property bool live: online && (bridge ? bridge.hasTrack : false)

  readonly property string host: setting("host", "127.0.0.1")
  readonly property int mediaPort: setting("mediaPort", 8080)
  readonly property int lyricsPort: setting("lyricsPort", 6555)
  readonly property string displayMode: setting("display", "lyric")
  readonly property real maxLabelWidth: setting("maxWidth", 280)
  readonly property bool showWhenIdle: setting("showWhenIdle", false)
  readonly property string scrollAction: setting("scrollAction", "track")

  readonly property string title: bridge ? bridge.title : ""
  readonly property string artist: bridge ? bridge.artist : ""
  readonly property string lyric: bridge ? bridge.lyric : ""
  readonly property string trackLabel: title || artist
    ? title + (artist ? "  ·  " + artist : "")
    : ""
  readonly property string trackLabelOrLyric: trackLabel !== "" ? trackLabel : lyric
  readonly property string labelText: !live || displayMode === "none" ? ""
    : displayMode === "track" ? trackLabelOrLyric
    : (lyric !== "" ? lyric : trackLabel)

  // 2.1+ sends `dur:`; when it is missing (2.0, or before the first update)
  // position over progress recovers the length.
  readonly property int displayDuration: !bridge ? 0
    : bridge.durationMs > 0 ? bridge.durationMs
    : bridge.progress > 0.02 && bridge.positionMs > 0 ? Math.round(bridge.positionMs / bridge.progress)
    : 0

  // The lyrics socket drives progress; fall back to the media socket's
  // position when only that one is up.
  readonly property real playProgress: !bridge ? 0
    : bridge.progress > 0 ? Math.min(1, bridge.progress)
    : bridge.durationMs > 0 ? Math.min(1, bridge.positionMs / bridge.durationMs)
    : 0

  property bool popupOpen: false
  function close() { popupOpen = false }
  function togglePopup() { popupOpen = !popupOpen }

  function fmtTime(ms) {
    var total = Math.max(0, Math.round(ms / 1000))
    var minutes = Math.floor(total / 60)
    var seconds = total % 60
    return minutes + ":" + (seconds < 10 ? "0" : "") + seconds
  }

  function playPause() {
    if (bridge) bridge.send(bridge.playing ? "pause" : "play")
  }

  visible: live || showWhenIdle || bridgeMissing
  implicitWidth: visible ? content.implicitWidth + Style.space(14) : 0
  implicitHeight: barSize

  // The service is mounted from the manifest, not by us, so the connection
  // settings live on the widget's shell.json entry and are pushed onto it.
  Binding {
    target: root.bridge
    property: "host"
    value: root.host
    when: root.bridge !== null
  }

  Binding {
    target: root.bridge
    property: "mediaPort"
    value: root.mediaPort
    when: root.bridge !== null
  }

  Binding {
    target: root.bridge
    property: "lyricsPort"
    value: root.lyricsPort
    when: root.bridge !== null
  }

  // Services mount a moment after the bar does; only call it missing once
  // that grace period has passed, so a cold start does not blink an error.
  Timer {
    id: bridgeGrace
    interval: 5000
    running: root.bridge === null
    onTriggered: root.bridgeMissing = root.bridge === null
  }

  onBridgeChanged: if (bridge) bridgeMissing = false

  IpcHandler {
    target: "openmediabridge.nowplaying"

    function toggle(): void { root.togglePopup() }
    function open(): void { root.popupOpen = true }
    function close(): void { root.close() }
    function playPause(): void { root.playPause() }
    function next(): void { if (root.bridge) root.bridge.send("next") }
    function prev(): void { if (root.bridge) root.bridge.send("prev") }
    function offsetUp(): void { if (root.bridge) root.bridge.sendLyrics("offset:+50") }
    function offsetDown(): void { if (root.bridge) root.bridge.sendLyrics("offset:-50") }
    function offsetSave(): void { if (root.bridge) root.bridge.sendLyrics("offset:save") }
    function toggleWordSync(): void { if (root.bridge) root.bridge.sendLyrics("toggle:wordsync") }
    function toggleTranslation(): void { if (root.bridge) root.bridge.sendLyrics("toggle:translation") }
    function nextSource(): void { if (root.bridge) root.bridge.sendLyrics("next") }
    function refreshLyrics(): void { if (root.bridge) root.bridge.sendLyrics("refresh") }
  }

  Row {
    id: content
    anchors.centerIn: parent
    spacing: Style.space(6)

    Text {
      id: glyph
      anchors.verticalCenter: parent.verticalCenter
      text: root.bridgeMissing ? "󰀪" : !root.live ? "󰝛" : root.bridge.playing ? "󰏤" : "󰐊"
      color: root.live && root.bridge.playing
        ? root.bar.barForeground
        : Qt.darker(root.bar.barForeground, 1.5)
      font.family: root.bar.fontFamily
      font.pixelSize: Style.font.body
      Behavior on color {
        enabled: !root.bar || root.bar.foregroundAnimationEnabled
        ColorAnimation { duration: 160 }
      }
    }

    Item {
      id: labelClip
      anchors.verticalCenter: parent.verticalCenter
      width: Math.min(root.maxLabelWidth, label.implicitWidth)
      height: glyph.height
      clip: true
      visible: !root.vertical && root.labelText !== ""

      Text {
        id: label
        width: labelClip.width
        anchors.verticalCenter: parent.verticalCenter
        color: root.bar.barForeground
        font.family: root.bar.fontFamily
        font.pixelSize: Style.font.body
        elide: Text.ElideRight

        // Lyric lines replace each other every few seconds, so the label
        // crossfades instead of scrolling — a marquee never finishes a line
        // before the next one lands.
        Component.onCompleted: text = root.labelText
      }
    }
  }

  Connections {
    target: root
    function onLabelTextChanged() {
      if (root.bar && root.bar.foregroundAnimationEnabled === false) {
        label.text = root.labelText
        return
      }
      swap.stop()
      swap.start()
    }
  }

  SequentialAnimation {
    id: swap
    NumberAnimation { target: label; property: "opacity"; to: 0; duration: 90; easing.type: Easing.OutQuad }
    PropertyAction { target: label; property: "text"; value: root.labelText }
    NumberAnimation { target: label; property: "opacity"; to: 1; duration: 150; easing.type: Easing.InQuad }
  }

  MouseArea {
    anchors.fill: parent
    hoverEnabled: true
    cursorShape: root.live ? Qt.PointingHandCursor : Qt.ArrowCursor
    acceptedButtons: Qt.LeftButton | Qt.RightButton | Qt.MiddleButton

    onClicked: function (mouse) {
      if (!root.bridge) return
      if (mouse.button === Qt.MiddleButton) root.bridge.send("next")
      else if (mouse.button === Qt.RightButton) root.popupOpen = !root.popupOpen
      else root.playPause()
    }

    onWheel: function (wheel) {
      if (!root.bridge) return
      var up = wheel.angleDelta.y > 0
      if (root.scrollAction === "offset") root.bridge.sendLyrics(up ? "offset:+50" : "offset:-50")
      else root.bridge.send(up ? "prev" : "next")
    }

    onEntered: {
      if (!root.bar) return
      root.bar.showTooltip(root, root.bridgeMissing
        ? "OpenMediaBridge: bridge service not loaded"
        : !root.online ? "OpenMediaBridge: not running"
        : !root.live ? "OpenMediaBridge: nothing playing"
        : root.trackLabelOrLyric)
    }
    onExited: if (root.bar) root.bar.hideTooltip(root)
  }

  PopupCard {
    id: popup
    anchorItem: root
    bar: root.bar
    owner: root
    open: root.popupOpen
    contentWidth: popup.fittedContentWidth(Style.space(360))
    contentHeight: popup.fittedContentHeight(column.implicitHeight)

    Column {
      id: column
      anchors.fill: parent
      spacing: Style.space(10)

      Row {
        spacing: Style.space(10)
        width: parent.width

        BorderSurface {
          width: Style.space(64)
          height: Style.space(64)
          radius: Style.spacing.labelGap
          color: Style.normalFillFor(root.bar.foreground, Color.accent)
          borderSpec: Border.controlSpec("normal", root.bar.foreground, Color.accent)

          Image {
            anchors.fill: parent
            anchors.margins: Style.space(2)
            fillMode: Image.PreserveAspectCrop
            asynchronous: true
            source: root.bridge && root.bridge.coverUrl ? root.bridge.coverUrl : ""
            visible: status === Image.Ready
          }

          Text {
            anchors.centerIn: parent
            visible: !root.bridge || !root.bridge.coverUrl
            text: "󰝚"
            color: root.bar.foreground
            font.family: root.bar.fontFamily
            font.pixelSize: Style.font.displayLarge
          }
        }

        Column {
          spacing: Style.space(4)
          width: parent.width - Style.space(74)

          Text {
            text: root.title || (root.live ? "Unknown track" : root.online ? "Nothing playing" : "OpenMediaBridge not running")
            color: root.bar.foreground
            font.family: root.bar.fontFamily
            font.pixelSize: Style.font.subtitle
            font.bold: true
            elide: Text.ElideRight
            width: parent.width
          }

          Text {
            text: root.artist
            color: Qt.darker(root.bar.foreground, 1.3)
            font.family: root.bar.fontFamily
            font.pixelSize: Style.font.bodySmall
            elide: Text.ElideRight
            width: parent.width
            visible: text !== ""
          }

          Text {
            text: {
              if (!root.bridge) return ""
              var parts = []
              if (root.bridge.album) parts.push(root.bridge.album)
              if (root.bridge.sourceName) parts.push(root.bridge.sourceName)
              return parts.join("  ·  ")
            }
            color: Qt.darker(root.bar.foreground, 1.6)
            font.family: root.bar.fontFamily
            font.pixelSize: Style.font.caption
            elide: Text.ElideRight
            width: parent.width
            visible: text !== ""
          }
        }
      }

      Item {
        width: parent.width
        height: track.height + timeRow.height + Style.space(4)
        visible: root.live

        Rectangle {
          id: track
          width: parent.width
          height: Math.max(3, Style.space(4))
          radius: height / 2
          color: Style.selectedFillFor(root.bar.foreground, Color.accent)

          Rectangle {
            width: parent.width * root.playProgress
            height: parent.height
            radius: parent.radius
            color: root.bar.foreground
            Behavior on width { NumberAnimation { duration: 400; easing.type: Easing.OutQuad } }
          }
        }

        Item {
          id: timeRow
          anchors.top: track.bottom
          anchors.topMargin: Style.space(4)
          width: parent.width
          height: elapsed.implicitHeight

          Text {
            id: elapsed
            anchors.left: parent.left
            text: root.bridge ? root.fmtTime(root.bridge.positionMs) : "0:00"
            color: Qt.darker(root.bar.foreground, 1.6)
            font.family: root.bar.fontFamily
            font.pixelSize: Style.font.caption
          }

          Text {
            anchors.right: parent.right
            text: root.fmtTime(root.displayDuration)
            color: Qt.darker(root.bar.foreground, 1.6)
            font.family: root.bar.fontFamily
            font.pixelSize: Style.font.caption
          }
        }
      }

      Row {
        anchors.horizontalCenter: parent.horizontalCenter
        spacing: Style.space(6)

        Button {
          iconText: "󰒮"
          foreground: root.bar.foreground
          horizontalPadding: Style.spacing.controlPaddingX
          verticalPadding: Style.spacing.controlPaddingY
          enabled: root.live
          opacity: enabled ? 1.0 : 0.4
          onClicked: if (root.bridge) root.bridge.send("prev")
        }

        Button {
          iconText: root.bridge && root.bridge.playing ? "󰏤" : "󰐊"
          foreground: root.bar.foreground
          horizontalPadding: Style.spacing.panelGap
          verticalPadding: Style.spacing.controlPaddingY
          iconSize: Style.font.iconLarge
          enabled: root.live
          opacity: enabled ? 1.0 : 0.4
          onClicked: root.playPause()
        }

        Button {
          iconText: "󰒭"
          foreground: root.bar.foreground
          horizontalPadding: Style.spacing.controlPaddingX
          verticalPadding: Style.spacing.controlPaddingY
          enabled: root.live
          opacity: enabled ? 1.0 : 0.4
          onClicked: if (root.bridge) root.bridge.send("next")
        }
      }

      PanelSeparator {
        visible: root.live
        foreground: root.bar.foreground
      }

      Column {
        width: parent.width
        spacing: Style.space(6)
        visible: root.live

        Text {
          width: parent.width
          text: root.lyric !== "" ? root.lyric : "…"
          color: root.lyric !== "" ? root.bar.foreground : Qt.darker(root.bar.foreground, 1.8)
          font.family: root.bar.fontFamily
          font.pixelSize: Style.font.body
          horizontalAlignment: Text.AlignHCenter
          wrapMode: Text.WordWrap
          maximumLineCount: 3
          elide: Text.ElideRight
        }

        Text {
          width: parent.width
          horizontalAlignment: Text.AlignHCenter
          color: Qt.darker(root.bar.foreground, 1.6)
          font.family: root.bar.fontFamily
          font.pixelSize: Style.font.caption
          text: {
            if (!root.bridge) return ""
            var parts = []
            parts.push(root.bridge.lyricSource ? root.bridge.lyricSource : "no lyrics")
            if (root.bridge.wordSync) parts.push("word sync")
            if (root.bridge.translating) parts.push("→ " + root.bridge.translateLang)
            return parts.join("  ·  ")
          }
        }
      }

      Row {
        anchors.horizontalCenter: parent.horizontalCenter
        spacing: Style.space(4)
        visible: root.live

        Button {
          text: "−500"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          onClicked: if (root.bridge) root.bridge.sendLyrics("offset:-500")
        }

        Button {
          text: "−50"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          onClicked: if (root.bridge) root.bridge.sendLyrics("offset:-50")
        }

        Text {
          anchors.verticalCenter: parent.verticalCenter
          width: Style.space(64)
          horizontalAlignment: Text.AlignHCenter
          text: (root.bridge ? root.bridge.offsetMs : 0) + " ms"
          color: root.bar.foreground
          font.family: root.bar.fontFamily
          font.pixelSize: Style.font.caption
        }

        Button {
          text: "+50"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          onClicked: if (root.bridge) root.bridge.sendLyrics("offset:+50")
        }

        Button {
          text: "+500"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          onClicked: if (root.bridge) root.bridge.sendLyrics("offset:+500")
        }

        Button {
          text: "Save"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          bordered: true
          onClicked: if (root.bridge) root.bridge.sendLyrics("offset:save")
        }
      }

      Row {
        anchors.horizontalCenter: parent.horizontalCenter
        spacing: Style.space(4)
        visible: root.live

        Button {
          text: "Word sync"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          selected: root.bridge ? root.bridge.wordSync : false
          onClicked: if (root.bridge) root.bridge.sendLyrics("toggle:wordsync")
        }

        Button {
          text: "Translate"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          selected: root.bridge ? root.bridge.translating : false
          onClicked: if (root.bridge) root.bridge.sendLyrics("toggle:translation")
        }

        Button {
          text: "Next source"
          foreground: root.bar.foreground
          fontSize: Style.font.caption
          onClicked: if (root.bridge) root.bridge.sendLyrics("next")
        }

        Button {
          iconText: "󰑐"
          foreground: root.bar.foreground
          iconSize: Style.font.body
          tooltipText: "Re-fetch lyrics"
          onClicked: if (root.bridge) root.bridge.sendLyrics("refresh")
        }
      }

      Text {
        width: parent.width
        horizontalAlignment: Text.AlignHCenter
        visible: root.bridgeMissing || !root.online
        wrapMode: Text.WordWrap
        color: Qt.darker(root.bar.foreground, 1.4)
        font.family: root.bar.fontFamily
        font.pixelSize: Style.font.bodySmall
        text: root.bridgeMissing
          ? "The bridge service did not load.\nInstall the Qt6 WebSockets QML module\n(QtWebSockets) for your distro."
          : "Waiting for OpenMediaBridge on " + root.host + ":" + root.mediaPort + " …"
      }
    }
  }
}
