#!/usr/bin/env bash
# Install this widget into the Omarchy shell.
#
#   ./install.sh          symlink the plugin (edits here go live on save)
#   ./install.sh --copy   copy it instead (for installing from a checkout you
#                         intend to delete)
#
# The plugin folder defaults to the Omarchy plugins dir under XDG config.
# Override it for a non-standard layout:
#   OMARCHY_PLUGINS_DIR=/path/to/plugins ./install.sh
set -euo pipefail

id="openmediabridge.nowplaying"
src="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
plugins_dir="${OMARCHY_PLUGINS_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/omarchy/plugins}"
dest="$plugins_dir/$id"
mode="link"

[[ "${1:-}" == "--copy" ]] && mode="copy"

# --- dependency checks -------------------------------------------------------

for bin in omarchy-shell omarchy; do
  if ! command -v "$bin" >/dev/null 2>&1; then
    echo "$bin not found — this widget needs the Omarchy shell." >&2
    exit 1
  fi
done

# Locate the QtWebSockets QML module. Its path differs per distro, so probe the
# common Qt6 qml roots (Arch /usr/lib, Fedora/openSUSE /usr/lib64, Debian/Ubuntu
# multiarch /usr/lib/<triplet>) instead of assuming one.
has_qtwebsockets() {
  local d
  for d in \
    /usr/lib/qt6/qml/QtWebSockets \
    /usr/lib64/qt6/qml/QtWebSockets \
    /usr/lib/*/qt6/qml/QtWebSockets \
    ${QML2_IMPORT_PATH:+${QML2_IMPORT_PATH//:/ }}; do
    [[ -d "$d" || -d "$d/QtWebSockets" ]] && return 0
  done
  find /usr/lib /usr/lib64 -maxdepth 5 -type d -name QtWebSockets -path '*qml*' \
    2>/dev/null | grep -q . && return 0
  return 1
}

# Best-effort install command for the distro's package manager.
qtws_install_hint() {
  if   command -v pacman       >/dev/null 2>&1; then echo "sudo pacman -S qt6-websockets"
  elif command -v apt          >/dev/null 2>&1; then echo "sudo apt install qml6-module-qtwebsockets"
  elif command -v dnf          >/dev/null 2>&1; then echo "sudo dnf install qt6-qtwebsockets"
  elif command -v zypper       >/dev/null 2>&1; then echo "sudo zypper install qt6-websockets-imports"
  elif command -v xbps-install >/dev/null 2>&1; then echo "sudo xbps-install qt6-websockets"
  else echo "install the Qt6 WebSockets QML module (QtWebSockets) for your distro"
  fi
}

if ! has_qtwebsockets; then
  echo "The Qt6 WebSockets QML module is missing. Install it first:" >&2
  echo "  $(qtws_install_hint)" >&2
  exit 1
fi

# --- install -----------------------------------------------------------------

if [[ "$src" == "$dest" ]]; then
  echo "Already installed at $dest — nothing to do." >&2
  exit 0
fi

mkdir -p "$plugins_dir"
rm -rf "$dest"

if [[ "$mode" == "copy" ]]; then
  cp -r "$src" "$dest"
else
  ln -s "$src" "$dest"
fi

omarchy-shell shell rescanPlugins >/dev/null
sleep 1
omarchy plugin enable "$id"

echo "Installed $id ($mode) at $dest."
echo "Move it around the bar with: omarchy bar move $id --section center"
