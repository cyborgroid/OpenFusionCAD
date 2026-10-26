#!/usr/bin/env bash
# 자동 검증: 커널 단위 테스트 + GUI 작업 흐름 시나리오(--autotest, X 디스플레이 필요)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CARGO="$(command -v cargo || true)"; [ -x "$HOME/.cargo/bin/cargo" ] && CARGO="$HOME/.cargo/bin/cargo"
"$CARGO" test --release --manifest-path "$ROOT/Cargo.toml" -p ofc-kernel
"$CARGO" build --release --manifest-path "$ROOT/Cargo.toml" -p ofc-kernel
dotnet build "$ROOT/app/OpenFusionCAD.csproj" -c Release >/dev/null
OUT="$(mktemp -d)"
"$ROOT/app/bin/Release/net10.0/linux-x64/openfusioncad" --autotest "$OUT" | grep -E "^(PASS|FAIL|END)"
grep -q "END autotest failures=0" "$OUT/autotest.log"
