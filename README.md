# OpenFusionCAD

Ubuntu에서 바로 실행되는 **파라메트릭 3D CAD**입니다. 화면 배치는 Autodesk Fusion과 같지만 코드는 처음부터 새로 작성했습니다. Fusion 바이너리, 아이콘, 로고는 사용하지 않습니다.

- **GUI**: C# · Avalonia · OpenGL(Silk.NET)
- **형상 커널**: Rust · OpenCASCADE 7.8(B-rep). 정적으로 링크되어 있어 시스템 OCCT가 필요 없습니다.
- **계정 연동**: Autodesk Platform Services(APS) 공식 OAuth(PKCE)

## Fusion 360 과의 일치

화면 배치·명령 이름(한국어판)·마우스 조작·기본 단축키를 Fusion 360 디자인 작업공간에 맞췄다.
항목별 대조와 구현 상태는 [docs/FUSION_PARITY.md](docs/FUSION_PARITY.md) 참고.

- **화면**: 응용프로그램 막대(데이터 패널 · 파일 ▾ · 저장 · 명령 취소/복구 · 문서 탭 ✕/+ · 확장 · 작업 상태 · 알림 · 도움말 · 사용자 이니셜),
  작업공간 `디자인 ▾`, 탭(솔리드 · 곡면 · 메쉬 · 판금 · 플라스틱 · 유틸리티 · 관리, 스케치 중 `스케치` 탭),
  그룹(작성 · 자동화 · 수정 · 조립 · 구성 · 검사 · 삽입 · 선택 / 스케치: 작성 · 수정 · 구속조건 · 검사 · 삽입 · 선택 · 스케치 마침),
  검색기(전구로 표시/숨기기, 문서 설정 · 명명된 뷰 · 원점 · 바디 · 스케치), 주석, ViewCube,
  탐색 막대(궤도 · 다음 보기 · 초점이동 · 줌 · 맞춤 · 화면표시 설정 ▾ · 그리드 및 스냅 ▾ · 뷰포트 ▾), 타임라인(끌 수 있는 기록 마커)
- **마우스**: 가운데 끌기 = 초점이동, Shift+가운데 끌기 = 궤도, 휠 = 줌, 왼→오 끌기 = 창 선택, 오→왼 끌기 = 교차 선택,
  **오른쪽 클릭 = 마킹 메뉴**
- **캔버스 조작기**: 돌출·면 간격띄우기의 파란 화살표를 끌어 거리 변경, 화살표 옆 상자에 값 입력

| 키 | 명령 | 키 | 명령 |
|---|---|---|---|
| S | 바로 가기 상자 (명령 검색) | L | 선 (스케치) |
| Q | 누르기/당기기 | R | 2점 직사각형 (스케치) |
| E | 돌출 | C | 중심 지름 원 (스케치) |
| H | 구멍 | D | 스케치 치수 (스케치) |
| F | 모깎기 | X | 보통/구성 (스케치) |
| M | 이동/복사 | T · O · P | 자르기 · 간격띄우기 · 투영 (자리만) |
| I | 측정 | 1 | 창 선택 |
| A | 모양 | J / Shift+J | 접합 / 같은 위치 접합 (자리만) |
| V | 가시성 전환 | F6 | 맞춤 |
| Delete | 삭제 | Ctrl+B | 모두 계산 |
| Esc | 명령 취소 / 선택 해제 | Ctrl+Z / Ctrl+Y | 명령 취소 / 명령 복구 |

## 기능 (0.2.0)

- **스케치**: 원점 평면·평평한 면, 선 · 2점/중심 직사각형 · 중심 지름 원 · 3점 호 · 외접/내접 폴리곤 · 점,
  보통/구성 선종류, 스케치 치수(선 길이 · 원 지름 · 직사각형 가로,세로), 점/그리드 스냅, 그리는 중 값 입력, 프로파일 자동 인식
- **작성**: 돌출(유형 · 시작: 프로파일 평면/간격띄우기 · 방향: 한쪽/양쪽/대칭 · 범위: 거리/전체 · 작업: 결합/잘라내기/교차/새 바디,
  거리 방향에 따라 결합↔잘라내기 자동 전환), 회전, 구멍(면 위 단일 구멍 · 거리/전체), 상자 · 원통 · 구
- **수정**: 누르기/당기기(면 → 면 간격띄우기, 모서리 → 모깎기), 모깎기 · 모따기, 쉘, 이동/복사(사본 작성), 삭제, 모양, 모두 계산
- **검사**: 측정(모서리 길이 · 면 면적 · 두 점 거리 · 바디 크기)
- **삽입**: STEP/IGES
- **타임라인**: 피처 편집 · 프로파일 스케치 편집 · 기록 마커 롤백(끌기) · 억제 · 이름 바꾸기 · 삭제
- **파일**: 새 디자인 · 열기 · 최근 문서 · 저장/다른 이름으로 저장(.ofcd) · 내보내기(STEP/STL/IGES/BREP) · 3D 인쇄(STL)
- 미구현 명령도 Fusion 과 같은 위치에 보이며, 실행하면 "다음 단계" 안내가 나온다 (메뉴에서 흐리게 표시)

## Autodesk 계정 연동

OpenFusionCAD는 Autodesk가 외부 앱에 공식 제공하는 로그인 방식(APS 3-legged OAuth, PKCE)을 사용합니다.

1. https://aps.autodesk.com 에서 앱을 만듭니다. 유형은 **Desktop, Mobile, Single-Page App**을 고릅니다.
2. Callback URL에 `http://localhost:8765/oauth/callback`을 등록합니다.
3. Data Management API를 사용하도록 설정합니다.
4. 앱 오른쪽 위 **로그인 → 계정 연동 설정**에서 Client ID를 입력합니다. 환경 변수 `OFC_APS_CLIENT_ID`로 지정해도 됩니다.

로그인은 기본 브라우저에서 진행됩니다. 로그인 후 **데이터 패널**에서 허브, 프로젝트, 폴더를 탐색하고 STEP/IGES/OFCD 파일을 열거나 현재 디자인에 삽입할 수 있습니다.

- 토큰은 `~/.config/openfusioncad/aps_token.json`(권한 600)에 저장됩니다.
- Fusion 전용 형식(`.f3d`)은 Autodesk 비공개 형식이라 열 수 없습니다. Fusion에서 STEP으로 내보낸 파일을 사용하십시오.

## 빌드와 설치

필요한 것: Rust(stable), .NET SDK 8 이상, CMake, g++. 첫 빌드는 OpenCASCADE를 컴파일하므로 시간이 오래 걸립니다.

```bash
scripts/build.sh            # dist/openfusioncad/ 와 dist/openfusioncad_<버전>_amd64.deb (.NET 런타임 포함)
sudo apt install ./dist/openfusioncad_*_amd64.deb
# 또는 sudo 없이 사용자 계정에만 설치
scripts/install-user.sh
scripts/test.sh             # 커널 단위 테스트 + GUI 작업 흐름 자동 검증 (X 디스플레이 필요)
```

## 구조

```
kernel/                    Rust 형상 커널 (별도 프로세스 ofc-kernel)
  src/model.rs             피처 타임라인 문서 모델 (= .ofcd 형식)
  src/sketch.rs            스케치 → 닫힌 프로파일(바깥 경계 + 구멍) 검출
  src/regen.rs             타임라인 재생성, 표시용 메시·모서리·면 정보
  src/protocol.rs          GUI ↔ 커널 stdin/stdout 이진 프로토콜
app/                       C# GUI
  Model/Document.cs        문서 모델 (커널과 같은 JSON)
  Kernel/KernelClient.cs   커널 프로세스 관리, 충돌 시 실패 피처 이분 탐색
  Viewport/                카메라, OpenGL 렌더러, 선택, ViewCube
  Sketching/               스케치 좌표계·기하
  UI/                      메인 창(툴바, 검색기, 타임라인, 명령, 스케치, 데이터 패널), --autotest
  Cloud/Aps.cs             Autodesk APS 로그인과 Data Management API
packaging/                 아이콘, .desktop, MIME(.ofcd)
scripts/                   build.sh, install-user.sh, test.sh
```

형상 커널이 별도 프로세스인 이유: OpenCASCADE는 잘못된 형상(예: 너무 큰 모깎기)에서 C++ 예외를 던져 프로세스를 종료시킬 수 있습니다. 커널을 분리해 두면 GUI는 그대로 살아 있습니다. GUI는 커널을 다시 띄우고 타임라인을 이분 탐색해 실패한 피처만 표시합니다.

## 알려진 제약

- 스케치 구속조건(평행, 수직, 치수 구속)은 아직 없습니다. 치수는 그릴 때 입력하는 값만 반영됩니다.
- 모깎기와 모따기 대상 모서리는 번호로 저장됩니다. 앞선 피처를 크게 바꾸면 다른 모서리를 가리킬 수 있습니다(토폴로지 이름 문제).
- 원통 구멍의 이음매(seam) 모서리가 선으로 보입니다.
- 클라우드는 읽기(탐색, 열기)만 지원합니다. 업로드는 다음 단계에서 구현합니다.
