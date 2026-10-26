# OpenFusionCAD — Fusion Client Downloader for Ubuntu

Autodesk `Fusion Client Downloader.exe`(Windows 스트리밍 설치기)를 분석해 **C#(GUI) + Rust(엔진)** 로 다시 만든
Ubuntu 용 설치기/런처입니다.

* 원본 GUI(860×500 창, 진행 배경 이미지 교차 페이드, 진행바, 오류/질의/사전 점검/설치제거/프록시 대화상자)를 같은 사양으로 재현했습니다.
* 공식 Autodesk CDN(`dl.appstreaming.autodesk.com`)에서 원본과 같은 프로토콜로 Fusion 패키지를 받고 SHA-1 로 검증합니다.
* **Autodesk 계정 연동 유지**: Identity Manager 설정(`ssoServer.guid`)을 갱신하고 브라우저 로그인 콜백
  `adskidmgr://` 을 Linux 표준 URL 처리기로 등록합니다.
* Windows/Microsoft 의존성(Python+VC++ 런타임, IE 웹뷰, COM, WMI, WScript, 레지스트리, WebView2, MSI 등)을 걷어내고 Linux 방식으로 바꿨습니다 → [docs/RESTRICTIONS.md](docs/RESTRICTIONS.md)
* 원본 분석 결과 → [docs/ANALYSIS.md](docs/ANALYSIS.md)

> Fusion 본체는 Autodesk 가 Windows/macOS 용으로만 배포합니다. 그래서 Fusion 실행에만 전용 Wine prefix 를 씁니다.
> 다운로더·업데이트·런처·로그인 콜백 처리는 모두 Linux 네이티브로 동작합니다.

## 구성

```
core/   Rust 엔진 (libofc_core.so + ofc-streamer CLI)
  src/constants.rs   원본에서 복원한 앱/플랫폼/위치/태스크 ID
  src/manifest.rs    full.json / packages/*.json 모델, ${location_…} 치환
  src/net.rs         이어받기·재시도·SHA-1 검증·프록시 인증
  src/registry.rs    원본과 같은 SQLite 설치 레지스트리 스키마
  src/process.rs     deploy/update/launch/uninstall/reinstall/cleancache/query/pull
  src/tasks.rs       설치 후처리(설정 활성화, 런처 ini, IDSDK, 외부 설치 …)
  src/wine.rs        전용 Wine prefix, DXVK, DLL override
  src/desktop.rs     .desktop 런처, URL 스킴(fusion360/adskidmgr), 아이콘
  src/precheck.rs    사전 점검 (Wine/디스크/네트워크/SSL/권한)
  src/cleancache.rs  원본 clean_cache_config.json 기반 캐시 정리
  src/i18n.rs        원본 messages.mo 로더 (한국어 등 11개 언어)
gui/    C# Avalonia GUI (원본 index.html/main.css/divdialog.css/processprogressink.js 재현)
resources/  원본 exe 에서 추출한 이미지·글꼴·번역·설정 (scripts/extract-assets.sh)
scripts/    extract-assets.sh, build.sh, install-user.sh
```

## 빌드

필요: Rust(stable), .NET SDK 8 이상, `7z`(p7zip-full), `dpkg-deb`

```bash
scripts/extract-assets.sh      # 원본 exe 에서 리소스 추출 (exe 가 프로젝트 루트에 있어야 함)
scripts/build.sh               # dist/openfusioncad/ 와 dist/openfusioncad_1.0.0_amd64.deb 생성
scripts/build.sh --self-contained   # .NET 런타임 포함 빌드
```

## 설치

```bash
sudo apt install wine64 wine32 xdg-utils mesa-vulkan-drivers   # Wine 9.0 이상
sudo apt install ./dist/openfusioncad_1.0.0_amd64.deb          # 시스템 설치 (/opt/openfusioncad)
# 또는 sudo 없이 사용자 설치 (~/.local/opt/openfusioncad)
scripts/install-user.sh
```

## 사용

앱 메뉴에서 **Fusion Client Downloader** 를 실행하면 원본과 같은 화면으로 Fusion 을 내려받아 설치하고 실행합니다.
설치가 끝나면 메뉴에 **Autodesk Fusion**, **Autodesk Fusion 제거** 항목이 생깁니다.

원본 `streamer.exe` 와 같은 인자를 받습니다.

```bash
fusion-client-downloader                         # 설치 (기본: -p deploy -a 73e72ada… -s production)
fusion-client-downloader -p update
fusion-client-downloader -p uninstall
fusion-client-downloader -p cleancache --cleancacheall --removeuserdata --removeusercred
fusion-client-downloader -p reinstall --deep_clean

ofc-streamer -p deploy --no-auto-launch          # 터미널/헤드리스 설치
ofc-streamer launch                              # 업데이트 확인 후 Fusion 실행
ofc-streamer -p query                            # 설치 정보(JSON)
ofc-streamer monitor                             # 진행 상황·아카이브별 다운로드/설치 과정을 표로 실시간 표시
ofc-streamer monitor --follow --all              # 다음 작업까지 계속 감시, 모든 행 표시
ofc-streamer -p pull --pull-dir ~/fusion-mirror  # 오프라인 미러 생성 → 이후 -c file://… 로 설치
```

Linux 전용 옵션: `--wine <경로>`, `--no-dxvk`, `--with-webview2`, `--no-desktop-shortcut`, `--installlocation <dir>`, `-v`

## 계정 로그인 흐름

1. Fusion 에서 로그인 → Wine 의 `winebrowser` 가 Linux 기본 브라우저로 Autodesk 로그인 페이지를 엽니다.
2. 로그인을 마치면 브라우저가 `adskidmgr:/…` 링크를 엽니다(“열기 허용”을 선택).
3. xdg 처리기 `openfusioncad-adskidmgr.desktop` → `ofc-streamer handle-uri` → Wine 의
   `AdskIdentityManager.exe "<url>"` 로 전달되어 실행 중인 Fusion 이 로그인됩니다.

## 파일 위치

| 항목 | 경로 |
|---|---|
| Wine prefix (Fusion 설치) | `~/.local/share/openfusioncad/wineprefix` (`…/AppData/Local/Autodesk/webdeploy/production/<버전>`) |
| 설치 레지스트리 | `~/.local/share/openfusioncad/registry.db` |
| 로그 | `~/.local/share/openfusioncad/logs/` (`fusion-wine.log` = Fusion 실행 로그) |
| 다운로드 캐시 | `~/.cache/openfusioncad/packages` (설치 후 자동 삭제) |
| 런처 | `~/.local/share/applications/openfusioncad-*.desktop` |
