#!/usr/bin/env bash
# 원본 "Fusion Client Downloader.exe"(7-Zip SFX)에서 GUI/로컬라이즈/설정 리소스만 추출해
# resources/ 에 배치한다. 실행 파일(.exe/.dll/.pyd)은 추출하지 않는다.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXE="${1:-$ROOT/Fusion Client Downloader.exe}"
OUT="$ROOT/resources"
[ -f "$EXE" ] || { echo "원본 설치기를 찾을 수 없습니다: $EXE" >&2; exit 1; }
command -v 7z >/dev/null || { echo "7z(p7zip-full)가 필요합니다: sudo apt install p7zip-full" >&2; exit 1; }

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
# 7-Zip은 SFX 스텁 뒤의 7z 아카이브를 직접 연다
7z x -y -o"$TMP/payload" "$EXE" 'resources/*' >/dev/null

rm -rf "$OUT"; mkdir -p "$OUT/html" "$OUT/locale"
SRC="$TMP/payload/resources"
cp -r "$SRC/html/res" "$OUT/html/"
cp "$SRC/Fusion.ico" "$SRC/autodesk.ico" "$OUT/"
cp "$SRC/precheck_config.json" "$SRC/clean_cache_config.json" "$SRC/buildinfo.json" "$OUT/"
for d in "$SRC"/*/LC_MESSAGES; do
  lang="$(basename "$(dirname "$d")")"
  mkdir -p "$OUT/locale/$lang/LC_MESSAGES"
  cp "$d/messages.mo" "$OUT/locale/$lang/LC_MESSAGES/"
done
# SFX 설정(앱 ID / 스트림 / 연결 URL)을 원본에서 그대로 읽어 기본값으로 저장
strings -n 8 "$EXE" | grep -E '^(Title|RunProgram|Progress)=' > "$OUT/sfx_config.txt"
echo "리소스 추출 완료: $OUT"
