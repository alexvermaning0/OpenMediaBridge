#!/usr/bin/env bash
# Install this widget into the Omarchy shell.
#
#   ./install.sh          symlink the plugin (edits here go live on save)
#   ./install.sh --copy   copy it instead (for installing from a checkout you
#                         intend to delete)
set -euo pipefail

id="openmediabridge.nowplaying"
src="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest="${XDG_CONFIG_HOME:-$HOME/.config}/omarchy/plugins/$id"
mode="link"

[[ "${1:-}" == "--copy" ]] && mode="copy"

if ! command -v omarchy-shell >/dev/null 2>&1; then
  echo "omarchy-shell not found — this widget needs the Omarchy shell." >&2
  exit 1
fi

if ! command -v omarchy >/dev/null 2>&1; then
  echo "omarchy CLI not found — this widget needs the Omarchy CLI to enable itself." >&2
  exit 1
fi

if ! [[ -d /usr/lib/qt6/qml/QtWebSockets ]]; then
  echo "QtWebSockets is missing. Install it first:" >&2
  echo "  sudo pacman -S qt6-websockets" >&2
  exit 1
fi

if [[ "$src" == "$dest" ]]; then
  echo "Already installed at $dest — nothing to do." >&2
  exit 0
fi

mkdir -p "$(dirname "$dest")"
rm -rf "$dest"

if [[ "$mode" == "copy" ]]; then
  cp -r "$src" "$dest"
else
  ln -s "$src" "$dest"
fi

omarchy-shell shell rescanPlugins >/dev/null
sleep 1
omarchy plugin enable "$id"

echo "Installed $id ($mode)."
echo "Move it around the bar with: omarchy bar move $id --section center"
