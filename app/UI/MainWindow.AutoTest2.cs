using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using OpenFusionCAD.Model;
using OpenFusionCAD.Sketching;

namespace OpenFusionCAD.UI;

/// --autotest 2부: Fusion 360 과 맞춘 단축키·명령·조작 검증
public partial class MainWindow
{
    /// 실제 KeyDown 처리 경로로 키 입력을 보낸다
    async Task PressKey(Key k, KeyModifiers mods = KeyModifiers.None)
    {
        OnKeyDown(this, new KeyEventArgs { Key = k, KeyModifiers = mods, RoutedEvent = KeyDownEvent, Source = Viewport });
        await Settle(350);
    }

    async Task RunParitySteps()
    {
        Log("BEGIN parity");
        await NewDocumentForce();
        // ---- Fusion 단축키 표
        Check(SolidKeys[Avalonia.Input.Key.Q] == "presspull" && SolidKeys[Avalonia.Input.Key.E] == "extrude" && SolidKeys[Avalonia.Input.Key.H] == "hole"
              && SolidKeys[Avalonia.Input.Key.F] == "fillet" && SolidKeys[Avalonia.Input.Key.M] == "move" && SolidKeys[Avalonia.Input.Key.I] == "measure"
              && SolidKeys[Avalonia.Input.Key.A] == "appearance" && SolidKeys[Avalonia.Input.Key.V] == "visibility" && SolidKeys[Avalonia.Input.Key.S] == "shortcutbox",
              "디자인 단축키 = Fusion 기본값 (Q E H F M I A V S)");
        Check(SketchKeys[Avalonia.Input.Key.L] == "line" && SketchKeys[Avalonia.Input.Key.R] == "rect2" && SketchKeys[Avalonia.Input.Key.C] == "circle"
              && SketchKeys[Avalonia.Input.Key.D] == "dimension" && SketchKeys[Avalonia.Input.Key.X] == "construction" && SketchKeys[Avalonia.Input.Key.T] == "trim",
              "스케치 단축키 = Fusion 기본값 (L R C D X T)");
        Check(SolidGroups.Select(g => g.Title).SequenceEqual(["작성", "자동화", "수정", "조립", "구성", "검사", "삽입", "선택"]), "솔리드 탭 그룹 순서 = Fusion");

        // ---- 상자 → 누르기/당기기(Q) 윗면
        StartCommand("box");
        await WaitCommandReady();
        await OkCommand(); await Settle();
        Check(Scene.Bodies.Count == 1, "상자 작성");
        Cam.Home(new Vector3(-5), new Vector3(25, 25, 25)); await Settle(500);
        await ClickWorld(new Vector3(10, 10, 20));              // 윗면 선택
        Check(Scene.SelectedFaces.Count == 1, "윗면 클릭 선택");
        await PressKey(Avalonia.Input.Key.Q);
        await WaitCommandReady();
        Check(_cmdFeature is PressPullFeature && _cmdFaces.Count == 1, "Q: 선택한 면으로 누르기/당기기 시작");
        Check(Scene.Manipulator != null, "캔버스 거리 조작기(화살표) 표시");
        SetManipValue(7, rebuild: true);
        await Settle(900);
        await Shot("10-presspull");
        await OkCommand(); await Settle();
        Check(Math.Abs(Scene.Bodies[0].BBox[5] - 27) < 1e-6 && Scene.Bodies[0].FaceCount == 6, $"누르기/당기기 +7 → 높이 27, 면 6개 (높이 {Scene.Bodies[0].BBox[5]:0.##}, 면 {Scene.Bodies[0].FaceCount})");

        // ---- 구멍(H): 윗면 클릭 지점
        await PressKey(Avalonia.Input.Key.H);
        await WaitCommandReady();
        await ClickWorld(new Vector3(10, 10, 27));
        ((HoleFeature)_cmdFeature!).Diameter = 6;
        ((HoleFeature)_cmdFeature!).Through = true;
        BuildCommandPanel(); SchedulePreview(0); await Settle(800);
        await OkCommand(); await Settle();
        Check(_last?.Features.All(f => f.Ok) == true && Scene.Bodies[0].FaceCount == 7, $"H: 관통 구멍 (면 {Scene.Bodies[0].FaceCount})");

        // ---- 쉘: 윗면 열기
        StartCommand("shell");
        await WaitCommandReady();
        await ClickWorld(new Vector3(3, 3, 27));
        ((ShellFeature)_cmdFeature!).Thickness = 2;
        SchedulePreview(0); await Settle(1200);
        await OkCommand(); await Settle(1200);
        Check(_last?.Features.All(f => f.Ok) == true && Scene.Bodies[0].FaceCount > 7, $"쉘 두께 2 (면 {Scene.Bodies[0].FaceCount})");
        await Shot("11-shell");

        // ---- 이동/복사(M) 사본
        Scene.SelectedBodies.Add(0);
        await PressKey(Avalonia.Input.Key.M);
        await WaitCommandReady();
        var mv = (MoveFeature)_cmdFeature!;
        mv.Translate[0] = 40; mv.Copy = true;
        SchedulePreview(0); await Settle(900);
        await OkCommand(); await Settle();
        Check(Scene.Bodies.Count == 2 && Math.Abs(Scene.Bodies[1].BBox[0] - 40) < 1e-6, "M: 이동/복사로 사본 (X+40)");

        // ---- 모양(A)은 대화상자라 직접 적용, 가시성(V), 삭제(Delete)
        _doc.Appearances["1"] = "plastic-red";
        await Regenerate();
        Check(Scene.BodyColors.TryGetValue(1, out var c) && c.X > 0.7f && c.Y < 0.3f, "모양: 바디2 = 플라스틱 빨강");
        Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(1);
        await PressKey(Avalonia.Input.Key.V);
        Check(Scene.HiddenBodies.Contains(1) && _doc.HiddenBodies.Contains(1), "V: 선택 바디 숨기기 (문서에 저장)");
        Scene.SelectedBodies.Add(1);
        await PressKey(Avalonia.Input.Key.V);
        Check(!Scene.HiddenBodies.Contains(1), "V: 다시 표시");
        Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(1);
        await PressKey(Avalonia.Input.Key.Delete);
        await Settle();
        Check(Scene.Bodies.Count == 1 && _doc.Features.Last() is RemoveFeature, "Delete: 바디 삭제 → 제거 피처");

        // ---- 마킹 메뉴 / 바로 가기 상자
        ShowMarkingMenu(new Point(400, 300));
        await Settle(200);
        Check(_marking.IsVisible && _marking.Children.Count >= 9, "오른쪽 클릭 마킹 메뉴 (원형 8 + 넘침 메뉴)");
        await Shot("12-marking-menu");
        HideMarkingMenu();
        await PressKey(Avalonia.Input.Key.S);
        ShortcutSearch.Text = "모깎";
        await Settle(200);
        Check(ShortcutBox.IsVisible && _sbItems.FirstOrDefault()?.Id == "fillet", "S: 바로 가기 상자 검색 '모깎' → 모깎기");
        await Shot("13-shortcut-box");
        HideShortcutBox();

        // ---- 측정(I): 모서리 길이
        await PressKey(Avalonia.Input.Key.I);
        await WaitCommandReady();
        await ClickWorld(new Vector3(10, 0, 0));
        Check(_cmdEdges.Count == 1 && MeasureResults().Any(r => r.Item1 == "모서리 길이" && r.Item2.StartsWith("20.000")), "I: 모서리 길이 20.000 mm");
        CancelCommand(); await Settle();

        // ---- 새 스케치 도구
        StartSketchPick(); await Settle(300);
        float size = OpenFusionCAD.Viewport.Picking.OriginPlaneSize * Cam.Distance / 200;
        await ClickWorld(new Vector3(size * 0.6f, -0.0f, size * 0.6f));   // XZ 평면
        Check(_mode == Mode.Sketch && ActiveSketch is { } xz && Math.Abs(new Frame(xz.Plane).N.Y) > 0.99f, "XZ 평면 스케치");
        Check(TabStrip.Children.Count == 8, "스케치 중 상황별 '스케치' 탭 추가 (탭 8개)");
        await Shot("14-sketch-tab");
        var f = new Frame(ActiveSketch!.Plane);
        SetSketchTool(SketchTool.CenterRect);
        await ClickWorld(f.To3(60, 10)); await ClickWorld(f.To3(66, 14));
        SetSketchTool(SketchTool.PolygonInscribed);
        await ClickWorld(f.To3(90, 10)); await ClickWorld(f.To3(95, 10));
        SetSketchTool(SketchTool.Point);
        await ClickWorld(f.To3(80, 30));
        var ents = ActiveSketch!.Entities;
        Check(ents.OfType<RectEntity>().Any(r => Math.Abs(Math.Abs(r.B[0] - r.A[0]) - 12) < 1e-6 && Math.Abs(Math.Abs(r.B[1] - r.A[1]) - 8) < 1e-6), "중심 직사각형 12 × 8");
        Check(ents.OfType<LineEntity>().Count() == 6, "내접 폴리곤 6변");
        Check(ents.OfType<PointEntity>().Count() == 1, "점");
        // 치수(D): 직사각형을 20,10 으로
        int ri = ents.FindIndex(e => e is RectEntity);
        SetSketchTool(SketchTool.Dimension);
        Scene.SelectedEntities.Clear(); Scene.SelectedEntities.Add(ri);
        await ApplyDimension([20, 10]);
        var rr = (RectEntity)ActiveSketch!.Entities[ri];
        Check(Math.Abs(Math.Abs(rr.B[0] - rr.A[0]) - 20) < 1e-6 && Math.Abs(Math.Abs(rr.B[1] - rr.A[1]) - 10) < 1e-6, "D: 스케치 치수로 20 × 10 변경");
        // X: 선 하나를 구성선으로
        int li = ActiveSketch!.Entities.FindIndex(e => e is LineEntity);
        Scene.SelectedEntities.Clear(); Scene.SelectedEntities.Add(li);
        await PressKey(Avalonia.Input.Key.X);
        Check(ActiveSketch!.Entities[li] is ConstructionEntity, "X: 보통 → 구성");
        // 창 선택: 전체를 왼→오로 감싸기
        SetSketchTool(SketchTool.Select);
        var pts = ActiveSketch!.Entities.SelectMany(SketchGeom.Polylines).SelectMany(p => p).Select(q => Cam.Project(f.To3(q.X, q.Y))).ToList();
        _downPos = new Point(pts.Min(p => p.X) - 10, pts.Min(p => p.Y) - 10);
        FinishBoxSelection(new Point(pts.Max(p => p.X) + 10, pts.Max(p => p.Y) + 10), KeyModifiers.None);
        Check(Scene.SelectedEntities.Count == ActiveSketch!.Entities.Count, $"창 선택으로 스케치 요소 전체 선택 ({Scene.SelectedEntities.Count}/{ActiveSketch!.Entities.Count})");
        ExitSketch(true); await Settle();
        Log("END parity");
    }

    async Task NewDocumentForce()
    {
        _dirty = false;
        await NewDocument();
        await Settle();
    }
}
