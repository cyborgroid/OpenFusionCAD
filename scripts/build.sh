#!/usr/bin/env bash
# OpenFusionCAD 빌드: Rust 엔진 + C# GUI → dist/openfusioncad, dist/openfusioncad_<ver>_amd64.deb
#   --self-contained : .NET 런타임을 포함해 배포 (대상 PC 에 .NET 불필요)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="1.0.0"
SC=false
[ "${1:-}" = "--self-contained" ] && SC=true
CARGO="${CARGO:-$(command -v cargo)}"; [ -x "$HOME/.cargo/bin/cargo" ] && CARGO="$HOME/.cargo/bin/cargo"
OUT="$ROOT/dist/openfusioncad"

[ -f "$ROOT/resources/precheck_config.json" ] || "$ROOT/scripts/extract-assets.sh"

echo "==> Rust 엔진 빌드"
"$CARGO" build --release --manifest-path "$ROOT/Cargo.toml"

echo "==> C# GUI 빌드"
rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish "$ROOT/gui/FusionClientDownloader.csproj" -c Release -r linux-x64 \
  --self-contained "$SC" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT" >/dev/null
rm -f "$OUT"/*.pdb

cp "$ROOT/target/release/ofc-streamer" "$ROOT/target/release/libofc_core.so" "$OUT/"
cp -r "$ROOT/resources" "$OUT/resources"
"$OUT/ofc-streamer" export-icon "$ROOT/resources/Fusion.ico" "$OUT/resources/fusion-client-downloader.png"

cat > "$OUT/fusion-client-downloader.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Fusion Client Downloader
Name[ko]=Fusion 클라이언트 다운로더
Comment=Install, update and launch Autodesk Fusion on Linux
Exec=/opt/openfusioncad/FusionClientDownloader %U
Icon=/opt/openfusioncad/resources/fusion-client-downloader.png
Terminal=false
Categories=Graphics;Engineering;
DESK

echo "==> .deb 패키지"
PKG="$ROOT/dist/deb"; rm -rf "$PKG"
mkdir -p "$PKG/DEBIAN" "$PKG/opt" "$PKG/usr/share/applications" "$PKG/usr/bin"
cp -r "$OUT" "$PKG/opt/openfusioncad"
cp "$OUT/fusion-client-downloader.desktop" "$PKG/usr/share/applications/"
ln -s /opt/openfusioncad/ofc-streamer "$PKG/usr/bin/ofc-streamer"
ln -s /opt/openfusioncad/FusionClientDownloader "$PKG/usr/bin/fusion-client-downloader"
DEPS="wine (>= 9.0) | wine-stable (>= 9.0) | winehq-stable, xdg-utils, libvulkan1"
$SC || DEPS="$DEPS, dotnet-runtime-10.0 | dotnet-runtime-9.0 | dotnet-runtime-8.0"
cat > "$PKG/DEBIAN/control" <<CTRL
Package: openfusioncad
Version: $VERSION
Section: graphics
Priority: optional
Architecture: amd64
Depends: $DEPS
Recommends: mesa-vulkan-drivers, fonts-noto-cjk
Maintainer: OpenFusionCAD <noreply@localhost>
Description: Linux-native Fusion Client Downloader (C#/Rust)
 Reimplementation of the Autodesk Fusion streaming installer for Ubuntu.
 Downloads Fusion from the official Autodesk CDN, installs it into a
 dedicated Wine prefix and keeps Autodesk account sign-in working.
CTRL
dpkg-deb --root-owner-group --build "$PKG" "$ROOT/dist/openfusioncad_${VERSION}_amd64.deb" >/dev/null
rm -rf "$PKG"
echo "완료:"
echo "  $OUT"
echo "  $ROOT/dist/openfusioncad_${VERSION}_amd64.deb"
