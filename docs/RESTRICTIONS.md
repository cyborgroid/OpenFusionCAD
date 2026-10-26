# Microsoft 환경 의존성과 제한 사항 정리

원본 설치기가 Windows/Microsoft 환경 때문에 갖던 의존성과 제약을 어떻게 처리했는지 정리한다.
**제거** = 더 이상 필요 없음, **대체** = 같은 목적의 Linux 표준 방식으로 교체, **선택** = 기본 꺼짐, 옵션으로 켤 수 있음.

## 1. 설치기(다운로더) 자체

| 원본 의존성 / 제약 | 처리 | OpenFusionCAD |
|---|---|---|
| 7-Zip SFX PE 스텁 (Windows 실행 파일) | 제거 | ELF 네이티브 실행 파일 + `.deb` 패키지 |
| Python 3.14 + VC++ 런타임 DLL 11종 (msvcp140, vcruntime140, concrt140, vcamp140, vccorlib140, vcomp140 …) | 제거 | Rust 엔진(`libofc_core.so`, `ofc-streamer`) |
| NuJSBridge 웹뷰(IE/Edge 기반 HTML 렌더링, `X-UA-Compatible: IE=edge`, IE 전용 CSS 필터) | 대체 | C#/Avalonia 네이티브 UI(Skia 렌더링). 레이아웃·색·크기·문구는 원본 HTML/CSS 와 동일 |
| `window.external.callNative` (COM 브리지) | 대체 | `xdg-open` |
| comtypes / `_wmi` / `propsys.tlb` (COM, WMI) | 제거 | 사용 안 함 |
| WScript(`uninstall.wsf`, `delayed_purge.wsf`, `shell_launch.js`), `cmd /C`, `.bat` | 대체 | `ofc-streamer -p uninstall`, 설치제거 `.desktop` 런처 |
| 동봉 `certs.pem` 고정 CA 번들 | 대체 | 시스템 CA 저장소(`/etc/ssl/certs`) |
| Windows 프록시 설정(WinHTTP/IE) | 대체 | `https_proxy`/`http_proxy` 환경 변수 + 407 시 자격 증명 대화상자(원본과 동일 UI) |
| 관리자 설치(`-g`, `%PROGRAMFILES%`, `icacls Everyone`) | 제거 | 사용자 단위 설치만 지원(`-g` 는 무시). 권한 상승 불필요 |
| `RunOnce` 레지스트리로 재부팅 후 파일 교체 | 제거 | 파일 잠금 개념이 다르므로 즉시 교체 |
| `diff.dll` 바이너리 패치 | 대체 | 변경된 패키지만 전체 아카이브 다운로드(SHA-1 검증, 이어받기) |
| 동시 실행 감지(Windows 뮤텍스/프로세스 목록) | 대체 | `flock` 잠금 파일 |

## 2. 사전 점검(precheck)

| 원본 점검 | 처리 | 비고 |
|---|---|---|
| CheckMediaFeaturePack (Windows N 에디션용 미디어 기능 팩) | 제거 | Windows 전용 제약 |
| CheckRegistryNotFound (Windows 레지스트리 접근) | 대체 | **CheckWine**: Wine 9.0 이상 존재 여부 |
| CHECK_SUPPORTED_OS (Windows 10 build 17763 이상) | 제거 | Wine prefix 가 Windows 10 으로 보고하도록 설정 |
| CheckFolderAccessibility | 유지 | 데이터/캐시 디렉터리 쓰기 권한 |
| CheckSSLCertificateIssue | 유지 | 시스템 CA 로 TLS 검증 |
| CheckNetworkConnectivity | 유지 | |
| DiskSpaceFull (8GB) | 유지 | `statvfs` |

## 3. 설치 후처리(Windows 셸 통합)

| 원본 | 처리 | OpenFusionCAD |
|---|---|---|
| 바탕화면/시작 메뉴 `.lnk`, 작업표시줄 고정, AppUserModelID | 대체 | `~/.local/share/applications/openfusioncad-fusion.desktop` (+ 바탕화면 복사, "Service Utility" 동작) |
| `ie4uinit -ClearIconCache`, `.ico` 배포 | 대체 | hicolor 아이콘 테마에 PNG 변환 저장 |
| URL 프로토콜 `fusion360:` (HKCU\Software\Classes) | 대체 | `x-scheme-handler/fusion360` (xdg-mime) |
| URL 프로토콜 `adskidmgr:` (계정 로그인 콜백) | 대체 | `x-scheme-handler/adskidmgr` → `ofc-streamer handle-uri` → Wine 의 `AdskIdentityManager.exe` |
| 프로그램 추가/제거 등록 | 대체 | "Autodesk Fusion 제거" `.desktop` 런처 |
| COM 셸 확장(썸네일, 속성 핸들러), MIME 레지스트리 | 제거 | Windows 탐색기 전용 |
| MSI 제품 제거(`msiexec /x`) | 제거 | |
| **Microsoft Edge WebView2 런타임 설치** | **선택** | 기본 설치 안 함. `--with-webview2` 로 Wine prefix 에만 설치 가능 |
| wine-mono(.NET 대체), wine-gecko(IE 대체) 자동 설치 | 제거 | prefix 생성 시 프롬프트 억제 |
| Autodesk 분석 세션 파일(`streamingsession.json`), ADP 분석 서비스(`adpclientservice.exe`) | 제거 | 텔레메트리 파일을 쓰지 않고, Wine 에서 멈춤을 일으키는 ADP 서비스는 DLL override 로 비활성화 |
| FusionLauncher 의 업데이트 확인(Windows streamer 호출) | 대체 | `ofc-streamer launch` 가 실행 전에 새 버전을 확인하고 GUI 업데이트 화면으로 전환 |

## 4. 남는 제약 (제거할 수 없는 것)

* **Fusion 본체는 Windows 바이너리다.** Autodesk 는 Linux 빌드를 배포하지 않는다(CDN 에는 Windows/macOS 플랫폼만 있다).
  따라서 Fusion 실행에는 Wine 이 필요하다. 다운로더/런처/업데이트/계정 콜백 처리는 모두 네이티브로 동작하며,
  Wine 은 Fusion 프로세스를 실행할 때만 쓰인다.
* Fusion 패키지 안에는 Autodesk 가 함께 배포하는 MSVC 런타임(MSVCRT 패키지)이 들어 있다. Fusion 실행에
  필요하므로 그대로 설치되며, 다운로더는 별도의 Microsoft 구성요소를 내려받지 않는다.
* Direct3D 렌더링은 오픈소스 **DXVK**(Vulkan)로 처리한다(`--no-dxvk` 로 끌 수 있음). Vulkan 드라이버가 필요하다.
* 일부 기능(예: WebView2 를 쓰는 패널)은 WebView2 를 설치하지 않으면 표시되지 않을 수 있다.
* Autodesk 의 라이선스/구독/로그인 정책은 그대로 적용된다. 이 프로그램은 공식 CDN 에서 공식 패키지를
  내려받고 공식 로그인 흐름을 연결할 뿐이며 인증·라이선스 검사를 변경하지 않는다.
* Wine 위에서 Fusion 의 동작은 Wine/GPU 드라이버 버전에 따라 달라질 수 있으며 Autodesk 의 공식 지원 대상이 아니다.
