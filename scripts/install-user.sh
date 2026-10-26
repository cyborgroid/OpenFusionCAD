#!/usr/bin/env bash
# sudo 없이 현재 사용자에게 설치 (~/.local/opt/openfusioncad, 메뉴 항목, .ofcd 파일 연결)
# 시스템 전체 설치는: sudo apt install ./dist/openfusioncad_*_amd64.deb
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/dist/openfusioncad"
[ -x "$SRC/openfusioncad" ] || "$ROOT/scripts/build.sh"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
DEST="$HOME/.local/opt/openfusioncad"
rm -rf "$DEST"; mkdir -p "$(dirname "$DEST")"; cp -r "$SRC" "$DEST"
mkdir -p "$HOME/.local/bin" "$DATA/applications" "$DATA/icons/hicolor/scalable/apps" "$DATA/mime/packages"
ln -sf "$DEST/openfusioncad" "$HOME/.local/bin/openfusioncad"
sed "s#/opt/openfusioncad/openfusioncad#$DEST/openfusioncad#" "$SRC/openfusioncad.desktop" > "$DATA/applications/openfusioncad.desktop"
cp "$SRC/openfusioncad.svg" "$DATA/icons/hicolor/scalable/apps/openfusioncad.svg"
cp "$SRC/openfusioncad-mime.xml" "$DATA/mime/packages/openfusioncad.xml"
update-mime-database "$DATA/mime" 2>/dev/null || true
update-desktop-database "$DATA/applications" 2>/dev/null || true
echo "설치됨: $DEST  (앱 메뉴의 'OpenFusionCAD' 또는 터미널에서 openfusioncad)"
