using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Input;
using OpenFusionCAD.Model;
using OpenFusionCAD.Sketching;
using OpenFusionCAD.Viewport;
using Plane = OpenFusionCAD.Model.Plane;

namespace OpenFusionCAD.UI;

/// <summary>
/// 숨김 `--autotest &lt;dir&gt;` 모드: 월드 좌표를 화면 좌표로 투영해 실제 마우스 처리 경로
/// (Hover → Click → Picking → 스케치/명령)를 그대로 호출하며 기본 작업 흐름을 검증한다.
/// 단계마다 "SHOT 이름" 을 출력하고 잠시 멈춰 외부에서 화면을 캡처할 수 있게 한다.
/// </summary>
public partial class MainWindow
{
    StreamWriter? _testLog;
    int _failures;

    void Log(string s)
    {
        Console.WriteLine(s);
        _testLog?.WriteLine(s);
        _testLog?.Flush();
    }

    void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Log($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    async Task Settle(int ms = 700)
    {
        await Task.Delay(ms);
        Viewport.Invalidate();
        await Task.Delay(100);
    }

    async Task Shot(string name)
    {
        Log($"SHOT {name}");
        await Task.Delay(1800);
    }

    async Task ClickWorld(Vector3 w)
    {
        var p = Cam.Project(w);
        Hover(p.X, p.Y);
        await Click(p.X, p.Y, KeyModifiers.None);
        await Settle(400);
    }

    async Task WaitCommandReady()
    {
        for (int i = 0; i < 100 && _cmdBase == null; i++) await Task.Delay(50);
        await Settle(300);
    }

    public async Task RunAutoTest(string dir)
    {
        Directory.CreateDirectory(dir);
        _testLog = new StreamWriter(Path.Combine(dir, "autotest.log"));
        Log("BEGIN autotest");
        try
        {
            await Regenerate();
            Cam.Home(new Vector3(-10), new Vector3(60, 40, 30));
            await Settle(600);

            // 1) XY 평면에 스케치: 원점 평면 선택 → 사각형 + 원
            StartSketchPick();
            await Settle(300);
            float size = Picking.OriginPlaneSize * Cam.Distance / 200;
            await ClickWorld(new Vector3(size * 0.6f, size * 0.6f, 0));
            Check(_mode == Mode.Sketch && ActiveSketch != null, "XY 평면 클릭으로 스케치 모드 진입");
            Check(ActiveSketch != null && new Frame(ActiveSketch.Plane).N.Z > 0.99f, "스케치 평면 법선 = +Z");
            Cam.Home(new Vector3(-10), new Vector3(60, 40, 30));
            SnapGrid.IsChecked = true; // 기본값: 그리드 스냅으로 정확한 치수
            await Settle(500);
            SetSketchTool(SketchTool.Rect);
            await ClickWorld(new Vector3(0, 0, 0));
            await ClickWorld(new Vector3(40, 30, 0));
            SetSketchTool(SketchTool.Circle);
            await ClickWorld(new Vector3(20, 15, 0));
            await ClickWorld(new Vector3(26, 15, 0));
            await Settle();
            var sk = ActiveSketch!;
            Check(sk.Entities.Count == 2, $"스케치 엔티티 2개 (실제 {sk.Entities.Count})");
            var profs = _last?.Profiles.GetValueOrDefault(sk.Id);
            Check(profs != null && profs.Count == 1 && profs[0].Holes.Count == 1, "프로파일 1개 (구멍 1개)");
            await Shot("01-sketch");
            ExitSketch(true);
            await Settle();

            // 2) 돌출: 프로파일 클릭 → 거리 15 → 확인
            StartCommand("extrude");
            await WaitCommandReady();
            Check(Scene.SelectedProfiles.Count == 1, "프로파일이 하나뿐이면 자동 선택");
            await ClickWorld(new Vector3(5, 5, 0));
            Check(Scene.SelectedProfiles.Count == 0, "프로파일 클릭 → 선택 해제(토글)");
            await ClickWorld(new Vector3(5, 5, 0));
            Check(Scene.SelectedProfiles.Count == 1, "프로파일 다시 클릭 → 선택");
            ((ExtrudeFeature)_cmdFeature!).Distance = 15;
            BuildCommandPanel();
            SchedulePreview(0);
            await Settle(900);
            await Shot("02-extrude-preview");
            await OkCommand();
            await Settle();
            Check(Scene.Bodies.Count == 1, "돌출 후 바디 1개");
            var b0 = Scene.Bodies.FirstOrDefault();
            Check(b0 != null && Math.Abs(b0.BBox[5] - 15) < 1e-6 && Math.Abs(b0.BBox[3] - 40) < 1e-6 && Math.Abs(b0.BBox[4] - 30) < 1e-6, $"바디 크기 40×30×15 (bbox {string.Join(",", b0?.BBox.Select(x => x.ToString("0.##")) ?? [])})");
            Check(b0 != null && b0.FaceCount == 7, $"면 7개 = 상자 6 + 구멍 1 (실제 {b0?.FaceCount})");

            // 3) 모깎기: 앞쪽 위 모서리 클릭
            StartCommand("fillet");
            await WaitCommandReady();
            await ClickWorld(new Vector3(20, 0, 15));
            Check(_cmdEdges.Count == 1, $"모서리 클릭으로 선택 ({_cmdEdges.Count})");
            ((FilletFeature)_cmdFeature!).Radius = 3;
            SchedulePreview(0);
            await Settle(900);
            await Shot("03-fillet-preview");
            await OkCommand();
            await Settle();
            Check(_last?.Features.All(f => f.Ok) == true, "모깎기 성공");

            // 4) 윗면에 스케치 → 원 → 잘라내기 돌출
            StartSketchPick();
            await Settle(300);
            await ClickWorld(new Vector3(34, 24, 15));
            var top = ActiveSketch;
            Check(top != null && new Frame(top.Plane).N.Z > 0.99f && Math.Abs(new Frame(top.Plane).O.Z - 15) < 1e-3, "윗면 클릭으로 면 위 스케치 (법선 +Z, 높이 15)");
            Cam.Home(new Vector3(-10), new Vector3(60, 40, 30));
            await Settle(500);
            SetSketchTool(SketchTool.Circle);
            await ClickWorld(new Vector3(33, 23, 15));
            await ClickWorld(new Vector3(36, 23, 15));
            ExitSketch(true);
            await Settle();
            StartCommand("extrude");
            await WaitCommandReady();
            Check(Scene.SelectedProfiles.Count == 1, "윗면 원 프로파일 자동 선택");
            var ex = (ExtrudeFeature)_cmdFeature!;
            ex.Operation = "cut"; ex.Distance = -8;
            BuildCommandPanel();
            SchedulePreview(0);
            await Settle(900);
            await OkCommand();
            await Settle();
            Check(Scene.Bodies.Count == 1 && _last?.Features.All(f => f.Ok) == true, "잘라내기 돌출 성공 (바디 1개 유지)");
            Check(Scene.Bodies[0].FaceCount > 9, $"구멍이 추가되어 면 증가 ({Scene.Bodies[0].FaceCount})");
            await Shot("04-final");

            // 5) 실행 취소/다시 실행
            int n = _doc.Features.Count;
            await Undo(); await Settle();
            Check(_doc.Features.Count == n - 1, "실행 취소로 마지막 피처 제거");
            await Redo(); await Settle();
            Check(_doc.Features.Count == n, "다시 실행으로 복구");

            // 6) 저장 / 내보내기 / 다시 열기
            _path = Path.Combine(dir, "autotest.ofcd");
            await Save();
            var step = Path.Combine(dir, "autotest.step");
            var err = await _kernel.ExportAsync(_doc, step, "step");
            Check(err == null && File.Exists(step) && new FileInfo(step).Length > 1000, $"STEP 내보내기 ({err ?? "ok"})");
            var stl = Path.Combine(dir, "autotest.stl");
            err = await _kernel.ExportAsync(_doc, stl, "stl");
            Check(err == null && File.Exists(stl), "STL 내보내기");
            var faces = Scene.Bodies[0].FaceCount;
            await OpenPath(_path);
            await Settle();
            Check(Scene.Bodies.Count == 1 && Scene.Bodies[0].FaceCount == faces, "저장한 .ofcd 다시 열기 – 같은 형상");

            // 7) 커널 충돌 복구: 너무 큰 모깎기
            Snapshot();
            InsertFeature(new FilletFeature { Name = "BadFillet", Body = 0, Edges = [0], Radius = 500 });
            await Commit();
            await Settle(1500);
            var bad = _last?.Features.FirstOrDefault(f => _doc.Find(f.Id)?.Name == "BadFillet");
            Check(bad is { Ok: false } && Scene.Bodies.Count == 1, $"실패 피처만 오류 표시, 나머지 유지 ({bad?.Error})");
            await Shot("05-failed-feature");
        }
        catch (Exception e)
        {
            _failures++;
            Log($"FAIL exception: {e}");
        }
        Log($"END autotest failures={_failures}");
        _testLog?.Dispose();
        _dirty = false;
        _confirmedClose = true;
        Close();
    }
}
