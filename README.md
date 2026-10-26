# OpenFusionCAD

Ubuntu에서 바로 실행되는 **파라메트릭 3D CAD**입니다. 화면 배치는 Autodesk Fusion과 같지만 코드는 처음부터 새로 작성했습니다. Fusion 바이너리, 아이콘, 로고는 사용하지 않습니다.

- **GUI**: C# · Avalonia · OpenGL(Silk.NET)
- **형상 커널**: Rust · OpenCASCADE 7.8(B-rep). 정적으로 링크되어 있어 시스템 OCCT가 필요 없습니다.
- **계정 연동**: Autodesk Platform Services(APS) 공식 OAuth(PKCE)

## 화면 구성 (Fusion과 같은 배치)

| 영역 | 내용 |
|---|---|
| 앱 바 | 데이터 패널, 파일 메뉴, 저장, 실행 취소/다시 실행, 문서 탭, 계정 상태, 도움말, 로그인 |
| 툴바 | 작업공간(디자인), 탭(솔리드 · 곡면 · 메쉬 · 판금 · 플라스틱 · 유틸리티 · 관리), 그룹(작성 · 수정 · 구성 · 검사 · 삽입 · 선택) |
| 검색기 | 문서 설정, 원점, 바디, 스케치. 눈 아이콘으로 표시/숨김 |
| 뷰포트 | 그라데이션 배경, 확대에 따라 간격이 바뀌는 그리드, 원점 축, 음영과 모서리선, 선택 하이라이트, ViewCube, 탐색 막대 |
| 스케치 모드 | 스케치 탭, 스케치 팔레트, 스케치 마치기 |
| 명령 대화상자 | 돌출, 회전, 모깎기 등. 값을 바꾸면 미리보기가 바로 갱신됩니다 |
| 타임라인 | 피처 아이콘, 마커(롤백), 편집, 억제, 이름 바꾸기, 삭제. 실패한 피처는 빨간색으로 표시됩니다 |

곡면, 메쉬, 판금, 플라스틱, 관리 탭은 자리만 있고 기능은 다음 단계에서 구현합니다.

## 기능 (0.1.0)

- **스케치**: 원점 평면(XY/XZ/YZ)이나 바디의 평평한 면 위에 그릴 수 있습니다.
  - 도구: 선, 2점 사각형, 중심-지름 원, 3점 호, 작도선
  - 점 스냅, 그리드 스냅, 그리는 중 숫자 입력으로 치수 지정(선 길이, `가로,세로`, 지름)
  - 닫힌 영역(구멍 포함)을 프로파일로 자동 인식합니다.
- **피처**
  - 돌출: 한쪽, 대칭, 양쪽 방향 / 새 바디, 결합, 잘라내기, 교차
  - 회전: 스케치 X축, Y축, 또는 선 축
  - 모깎기, 모따기: 모서리를 클릭해 선택
  - 기본 형상: 상자, 원통, 구
  - STEP/IGES 삽입
- **타임라인**: 모든 피처를 다시 편집할 수 있고, 수정하면 전체 모델이 다시 계산됩니다.
- **파일**
  - `.ofcd`(JSON) 저장 및 열기, 최근 문서
  - STEP, STL, IGES, BREP 내보내기
  - STEP/IGES 열기
- **조작**
  - 휠: 커서 위치 기준 확대/축소
  - 가운데 버튼 드래그: 화면 이동
  - Shift + 가운데 버튼 드래그, 또는 오른쪽 버튼 드래그: 회전
  - ViewCube, 홈 뷰, 맞춤, 원근/직교 투영 전환
- **단축키**: 아래 표와 같습니다.

| 단축키 | 동작 | 단축키 | 동작 |
|---|---|---|---|
| L | 선 | E | 돌출 |
| R | 사각형 | F | 모깎기 |
| C | 원 | Del | 삭제 |
| A | 호 | Esc | 취소 |
| Ctrl+Z / Y | 실행 취소 / 다시 실행 | Ctrl+S / O / N | 저장 / 열기 / 새 디자인 |

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
scripts/build.sh            # dist/openfusioncad/ 와 dist/openfusioncad_0.1.0_amd64.deb (.NET 런타임 포함)
sudo apt install ./dist/openfusioncad_0.1.0_amd64.deb
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
