# 원본 `Fusion Client Downloader.exe` 분석

## 1. 파일 구조

| 항목 | 내용 |
|---|---|
| 형식 | PE32 (i386) GUI, 링커 6.0 — **7-Zip SfxSetup 스텁** + 7z 아카이브(LZMA:25 BCJ2, 13.3MB) |
| 버전 정보 | FileVersion 14.2706.1.0, "Self extractor for Autodesk Streaming installer based on 7-zip SfxSetup" |
| SFX 설정 | `Title="Fusion Client Downloader"`<br>`RunProgram="streamer.exe -a 73e72ada57b7480280f7a6f4a289729f -s production -c https://dl.appstreaming.autodesk.com/production/"` |

압축 해제 결과(37MB):

```
streamer.exe            cx_Freeze 로 동결된 Python 3.14 런처 (빌드 c039-winf4db10, 2026-10-21)
python314.dll, msvcp140*.dll, vcruntime140*.dll, concrt140.dll, vcamp140.dll, vccorlib140.dll, vcomp140.dll
diff.dll, zlib1.dll     바이너리 패치(차등 업데이트)
certs.pem               동봉 CA 번들
lib/                    Python 표준 라이브러리 + comtypes, apsw(SQLite), _wmi, NuJSBridgePythonExtension10.pyd
lib/adsk/dls/streamer/  설치기 본체 (.pyc 73개)
resources/html/         GUI: index.html, main.css, divdialog.css, processprogressink.js, jQuery/jQuery UI
resources/<lang>/LC_MESSAGES/messages.mo   다국어(ko 포함 11개)
resources/scripts/windows/*.wsf|*.js|*.bat  WScript 기반 설치제거/지연 삭제
resources/scripts/reg/*.sql                  설치 레지스트리 SQLite 스키마
resources/typelibs/propsys.tlb               Windows Shell 속성 시스템 COM 타입 라이브러리
```

Python 바이트코드는 3.14 형식이라 기존 디컴파일러가 동작하지 않아 `marshal` + `dis` 로 디스어셈블해 정적 분석했다(코드 실행 없음).

## 2. GUI 사양 (index.html / main.css / divdialog.css / processprogressink.js)

GUI 는 `NuJSBridgePythonExtension10.pyd`(Windows 전용 웹뷰 브리지)가 띄우는 HTML 페이지이며, Python 측이
`ProcessProgressSink` 의 메서드를 호출하고 `getresult()` 를 폴링해 응답을 받는다.

| 요소 | 사양 |
|---|---|
| 창 | 860×500 (`.productlogo`), 우클릭 메뉴 차단, 스크롤 없음 |
| 배경 | `res/Deploy/<appid>/0..6.png` 7장(설치), 그 외 `res/infotainment/<appid>/0.png`. `imgId = min(n-1, floor(n·pct/100))`, `#backbuff` 에 프리로드 후 1200ms fadeIn 교체 |
| 진행 영역 | `top:400px`, 설명(Tahoma 굵게 #4D4D4D, 흰색 4방향 text-shadow), 진행바(폭 30%, 높이 22, 불투명도 0.7, jQuery UI smoothness), `N%` 라벨(바 위 겹침). 초기 상태는 불확정(value:false) |
| 오류 대화상자 | `.hide-error-msg`: top 2.5%, left 20%, 폭 500, 제목줄 78px(22px Light #6B6B6B), "What's wrong?/What should I do?" 2열(113/299px), 하단 118px, `Open Log File Location`(보조) + `OK`(주, #00A5E1) |
| 질의 대화상자 | `.hide-query-msg`: top 13%, 같은 스타일, `No`/`Yes`. 안내(informuser)는 Yes 만 |
| 사전 점검 | `#precheck-dialog`: 검은 패널(50,140,50,150), 빨간 ⊗ 아이콘, 항목별 회색(#898989) 셀 + "Learn more.." 링크, `Re-Test`/`Cancel` |
| 설치제거 설문 | `.uninstall-survey`: 검은 패널(불투명도 0.9), "We are sorry to see you go.", `Uninstall`/`Cancel` |
| 프록시 로그인 | jQuery UI 모달: Username/Password, `Ok`/`Cancel` |
| 공통 | 제목이 382px 초과 시 제목줄 108px, 제목줄 드래그로 이동, 링크는 `window.external.callNative('1', url)`, 로그 폴더는 `callNative('3', dir, file)` |

## 3. 다운로드/설치 프로토콜

```
{root} = https://dl.appstreaming.autodesk.com/production/
{platform} = 67316f5e79bc48318aa5f7b6bb58243d (Windows) | 97e6dd95735340d6ad6e222a520454db (macOS)

{root}{platform}/{appid}/full.json          애플리케이션 매니페스트 (build-version, release-version, packages[])
{root}packages/{checksum}.json              패키지 매니페스트 (non-patched[], root 트리, references, *-deploy-tasks)
{root}packages/{non-patched}.tar.xz         실제 내용. 파일명 = 아카이브 SHA-1
```

* 2026-10-21 production: build 2706.1.14, 패키지 84개, 압축 1.58GB / 설치 6.1GB.
* 패키지 `root.destination` 은 LocationIds(`82533b…`=Common 등) 또는 버전별 해시이며 해시는
  `%LOCALAPPDATA%\Autodesk\webdeploy\<stream>\<hash>` 로 풀린다.
* `${location_<id>}`, `${dirsep}`, `${streamtag}`(production 이면 빈 문자열, 그 외 `[stream]`) 치환.
* 설치 기록은 SQLite(`tblSTREAM / tblAPPLICATIONS / tblPACKAGES / tblAPPLICATIONPACKAGES / tblFILES`).

## 4. 계정 연동 (Autodesk ID)

설치기 자체는 로그인하지 않는다. 계정 연동은 다음 두 요소로 이루어진다.

1. **IDSDK 패키지**: `Autodesk Identity Manager/AdskIdentityManagerInstall.config` (파일 ID `2359d677…`).
   설치 후 `UPDATE_IDSDK_CONFIG` 태스크가 `ssoServer.guid` 에 새 UUID 를 기록한다.
2. **로그인 콜백**: Fusion 이 기본 브라우저로 Autodesk 로그인 페이지를 열고, 로그인이 끝나면 브라우저가
   `adskidmgr:` URL 로 리디렉션한다. Windows 에서는 레지스트리의 URL 프로토콜 처리기가
   `AdskIdentityManager.exe "<url>"` 를 실행해 실행 중인 Fusion 에 토큰을 전달한다.

## 5. 후처리 태스크 (SpecialActionIds)

| 태스크 | Windows 동작 |
|---|---|
| ACTIVATE_CONFIG | `ApplicationOptions.<stream>.json` → `Fusion 360.server.config` 복사 |
| PROCESS_TEMPLATE | `Fusion 360.streamer.config` 의 `${…}` 치환 |
| WRITE_STREAMER_OPTIONS | 같은 파일에 StreamerRootUrl/ETag/InstallStream/InstallOrigin 기록 |
| WRITE_LAUNCHER_INI | `FusionLauncher.exe.ini`(UTF-16): stream/auid/cmd/global |
| UPDATE_PINNABLE_LAUNCHER | FusionLauncher.exe 를 고정 경로(`…/6a0c9611…/`)로 복사, 실패 시 RunOnce |
| CREATE_LAUNCH_ITEMS / DELIVER_ICON | 바탕화면·시작 메뉴 .lnk, 작업표시줄 고정, 아이콘 |
| FILE_ASSOCIATIONS | `HKCU\Software\Classes\fusion360` URL 프로토콜 |
| REGISTER_UNINSTALL | `HKCU\…\Uninstall\…` + `wscript.exe uninstall.wsf` |
| ADD_REGISTRY_INFO / REGISTER_COM | 썸네일/속성 셸 확장 COM 등록 (`{C4F0910E…}` Neutron Image Extractor 등) |
| INSTALL_WEBVIEW2 | `MicrosoftEdgeWebView2RuntimeInstallerX64.exe /silent /install` |
| EXTERNAL_INSTALLS | `MaterialLibraryMigrator.exe migratecache "<Common>\Material Library"` 등 |
| WRITE_GA_APPNAME | 분석 세션 파일 `streamingsession.json` |
| CHECK_SUPPORTED_OS | Windows 10 build 17763 이상 |
| FULL_CHECKSUM_VALIDATION | 전체 체크섬 검증 |
