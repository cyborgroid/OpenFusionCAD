#!/usr/bin/env bash
# OpenFusionCAD 빌드: Rust 형상 커널 + C# GUI → dist/openfusioncad/, dist/openfusioncad_<ver>_amd64.deb
#   --framework-dependent : .NET 런타임을 포함하지 않음 (대상 PC 에 dotnet-runtime 필요)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/app/OpenFusionCAD.csproj")"
SC=true
[ "${1:-}" = "--framework-dependent" ] && SC=false
CARGO="$(command -v cargo || true)"; [ -x "$HOME/.cargo/bin/cargo" ] && CARGO="$HOME/.cargo/bin/cargo"
[ -n "$CARGO" ] || { echo "Rust(cargo)가 필요합니다: https://rustup.rs" >&2; exit 1; }
command -v dotnet >/dev/null || { echo ".NET SDK 8 이상이 필요합니다" >&2; exit 1; }
OUT="$ROOT/dist/openfusioncad"

echo "==> 형상 커널 (Rust + OpenCASCADE, 첫 빌드는 OCCT 컴파일로 오래 걸림)"
"$CARGO" build --release --manifest-path "$ROOT/Cargo.toml" -p ofc-kernel

echo "==> GUI (C# Avalonia)"
rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish "$ROOT/app/OpenFusionCAD.csproj" -c Release -r linux-x64 --self-contained "$SC" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$OUT" >/dev/null
rm -f "$OUT"/*.pdb
cp "$ROOT/target/release/ofc-kernel" "$OUT/"
cp "$ROOT/packaging/openfusioncad.svg" "$ROOT/packaging/openfusioncad-mime.xml" "$OUT/"
sed "s#@BIN@#/opt/openfusioncad/openfusioncad#" "$ROOT/packaging/openfusioncad.desktop" > "$OUT/openfusioncad.desktop"

echo "==> .deb"
PKG="$ROOT/dist/deb"; rm -rf "$PKG"
mkdir -p "$PKG/DEBIAN" "$PKG/opt" "$PKG/usr/bin" "$PKG/usr/share/applications" \
         "$PKG/usr/share/icons/hicolor/scalable/apps" "$PKG/usr/share/mime/packages"
cp -r "$OUT" "$PKG/opt/openfusioncad"
ln -s /opt/openfusioncad/openfusioncad "$PKG/usr/bin/openfusioncad"
cp "$OUT/openfusioncad.desktop" "$PKG/usr/share/applications/"
cp "$OUT/openfusioncad.svg" "$PKG/usr/share/icons/hicolor/scalable/apps/"
cp "$OUT/openfusioncad-mime.xml" "$PKG/usr/share/mime/packages/openfusioncad.xml"
DEPS="libc6 (>= 2.35), libstdc++6, libgl1, libx11-6, libice6, libsm6, libfontconfig1, xdg-utils"
$SC || DEPS="$DEPS, dotnet-runtime-10.0 | dotnet-runtime-9.0 | dotnet-runtime-8.0"
cat > "$PKG/DEBIAN/control" <<CTRL
Package: openfusioncad
Version: $VERSION
Section: graphics
Priority: optional
Architecture: amd64
Depends: $DEPS
Recommends: fonts-noto-cjk
Maintainer: OpenFusionCAD <noreply@localhost>
Description: Parametric 3D CAD for Ubuntu (C#/Rust, OpenCASCADE)
 Sketch-based solid modeling with a feature timeline: extrude, revolve,
 fillet, chamfer, primitives, STEP/IGES import and STEP/STL/IGES export.
 Optional sign-in with an Autodesk account (APS) to open cloud files.
CTRL
cat > "$PKG/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
command -v update-mime-database >/dev/null && update-mime-database /usr/share/mime || true
command -v update-desktop-database >/dev/null && update-desktop-database /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
POST
chmod 755 "$PKG/DEBIAN/postinst"
dpkg-deb --root-owner-group --build "$PKG" "$ROOT/dist/openfusioncad_${VERSION}_amd64.deb" >/dev/null
rm -rf "$PKG"
echo "완료:"
echo "  $OUT"
echo "  $ROOT/dist/openfusioncad_${VERSION}_amd64.deb"
