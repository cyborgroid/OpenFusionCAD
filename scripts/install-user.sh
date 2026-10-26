#!/usr/bin/env bash
# sudo 없이 현재 사용자에게 설치: ~/.local/opt/openfusioncad + 메뉴 항목 + ~/.local/bin 링크
# (시스템 전체 설치는 dist/openfusioncad_*.deb 를 sudo apt install ./... 로 설치)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/dist/openfusioncad"
[ -x "$SRC/ofc-streamer" ] || "$ROOT/scripts/build.sh"
DEST="${XDG_DATA_HOME:-$HOME/.local/share}/../opt/openfusioncad"
DEST="$(mkdir -p "$DEST" && cd "$DEST" && pwd)"
rm -rf "$DEST"; cp -r "$SRC" "$DEST"
mkdir -p "$HOME/.local/bin" "${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ln -sf "$DEST/ofc-streamer" "$HOME/.local/bin/ofc-streamer"
ln -sf "$DEST/FusionClientDownloader" "$HOME/.local/bin/fusion-client-downloader"
sed -e "s#/opt/openfusioncad#$DEST#g" "$SRC/fusion-client-downloader.desktop" \
  > "${XDG_DATA_HOME:-$HOME/.local/share}/applications/fusion-client-downloader.desktop"
update-desktop-database "${XDG_DATA_HOME:-$HOME/.local/share}/applications" 2>/dev/null || true
echo "설치됨: $DEST"
echo "실행: 앱 메뉴의 'Fusion Client Downloader' 또는  fusion-client-downloader"
