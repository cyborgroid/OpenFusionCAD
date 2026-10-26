using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFusionCAD.Kernel;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Sketching;
using OpenFusionCAD.Viewport;

namespace OpenFusionCAD.UI;

/// <summary>
/// 명령 대화상자 (Fusion 360 과 같은 이름·행 구성). 실시간 미리보기, 캔버스 조작기,
/// 선택(프로파일/모서리/면/바디) 처리.
/// </summary>
public partial class MainWindow
{
    string? _cmd;
    Feature? _cmdFeature;
    int _cmdEditIndex = -1;
    string? _cmdSketchId;
    RegenResult? _cmdBase;          // 이 피처를 적용하기 전 모델 (모서리/면 선택 기준)
    EdgeRef? _cmdHoverEdge;
    FaceRef? _cmdHoverFace;
    readonly HashSet<EdgeRef> _cmdEdges = [];
    readonly HashSet<FaceRef> _cmdFaces = [];
    readonly List<(Vector3 p, string what)> _measurePicks = [];
    DispatcherTimer? _previewTimer;
    TextBlock? _selLabel;
    bool _opAuto = true;            // 작업을 사용자가 바꾸기 전까지 거리 부호로 결합/잘라내기 자동 전환

    static readonly (string key, string label)[] Ops = [("join", "결합"), ("cut", "잘라내기"), ("intersect", "교차"), ("new", "새 바디")];

    void HookCommandPanel()
    {
        CommandOk.Click += async (_, _) => await OkCommand();
        CommandCancel.Click += (_, _) => CancelCommand();
        ManipInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (double.TryParse((ManipInput.Text ?? "").Replace("mm", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) SetManipValue(v, rebuild: true);
                Viewport.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { Viewport.Focus(); e.Handled = true; }
        };
    }

    static string KindTitle(string kind) => kind switch
    {
        "Extrude" => "돌출", "Revolve" => "회전", "Fillet" => "모깎기", "Chamfer" => "모따기",
        "Box" => "상자", "Cylinder" => "원통", "Sphere" => "구", "PressPull" => "면 간격띄우기",
        "Hole" => "구멍", "Shell" => "쉘", "Move" => "이동/복사", "Measure" => "측정", _ => kind,
    };

    // ------------------------------------------------------------------ 누르기/당기기 (Q)

    /// Q: 선택에 따라 다른 명령 – 모서리 → 모깎기, 평평한 면 → 면 간격띄우기
    void StartPressPull()
    {
        if (Scene.SelectedEdges.Count > 0) { StartCommand("fillet"); return; }
        StartCommand("presspull");
    }

    // ------------------------------------------------------------------ 시작 / 편집

    public async void StartCommand(string kind, Feature? edit = null)
    {
        if (_mode == Mode.Sketch) ExitSketch(false);
        if (_mode == Mode.PickSketchPlane) EndSketchPick();
        if (_mode == Mode.Command) CancelCommand(regen: false);
        var preFaces = Scene.SelectedFaces.ToList();
        var preEdges = Scene.SelectedEdges.ToList();
        var preBodies = Scene.SelectedBodies.ToList();
        _cmd = kind;
        _cmdEditIndex = edit != null ? _doc.Features.IndexOf(edit) : -1;
        _cmdFeature = edit != null ? Document.FromJson(new Document { Features = [edit] }.ToJson()).Features[0] : kind switch
        {
            "extrude" => new ExtrudeFeature { Operation = Scene.Bodies.Count > 0 ? "join" : "new" },
            "revolve" => new RevolveFeature { Operation = Scene.Bodies.Count > 0 ? "join" : "new" },
            "fillet" => new FilletFeature { Radius = 1 },
            "chamfer" => new ChamferFeature { Distance = 1 },
            "box" => new BoxFeature(),
            "cylinder" => new CylinderFeature(),
            "sphere" => new SphereFeature(),
            "presspull" => new PressPullFeature { Distance = 5 },
            "hole" => new HoleFeature { Diameter = 5, Depth = 10 },
            "shell" => new ShellFeature { Thickness = 1 },
            "move" => new MoveFeature(),
            "measure" => new MeasureFeature(),
            _ => null,
        };
        if (_cmdFeature == null) return;
        if (edit == null) _cmdFeature.Name = _doc.NextName(_cmdFeature.Kind);
        _opAuto = edit == null;

        // 기준 모델: 이 피처 직전까지
        var baseDoc = _doc.Clone();
        baseDoc.Marker = _cmdEditIndex >= 0 ? _cmdEditIndex : _doc.ActiveCount;
        _cmdBase = null;
        _mode = Mode.Command;
        _cmdBase = await _kernel.RegenerateAsync(baseDoc);
        if (_mode != Mode.Command || _cmd != kind) return;

        _cmdEdges.Clear(); _cmdFaces.Clear(); _measurePicks.Clear();
        _cmdSketchId = null;
        Scene.ClearSelection();
        switch (_cmdFeature)
        {
            case ExtrudeFeature ex: InitProfileSelection(ex.Sketch, ex.Profiles); break;
            case RevolveFeature rv: InitProfileSelection(rv.Sketch, rv.Profiles); break;
            case FilletFeature fl when edit != null: foreach (var e in fl.Edges) _cmdEdges.Add(new EdgeRef(fl.Body, e)); break;
            case ChamferFeature ch when edit != null: foreach (var e in ch.Edges) _cmdEdges.Add(new EdgeRef(ch.Body, e)); break;
            case FilletFeature or ChamferFeature: foreach (var e in preEdges) _cmdEdges.Add(e); break;
            case PressPullFeature pp when edit != null: _cmdFaces.Add(new FaceRef(pp.Body, pp.Face)); break;
            case PressPullFeature: foreach (var f in preFaces.Where(IsBasePlanar).Take(1)) _cmdFaces.Add(f); break;
            case ShellFeature sh when edit != null: foreach (var f in sh.Faces) _cmdFaces.Add(new FaceRef(sh.Body, f)); break;
            case ShellFeature: foreach (var f in preFaces) _cmdFaces.Add(f); break;
            case HoleFeature when edit == null && preFaces.FirstOrDefault() is var hf && preFaces.Count > 0 && IsBasePlanar(hf): SetHoleFromFace(hf, null); break;
            case MoveFeature mv when edit == null:
                var target = preBodies.Concat(preFaces.Select(f => f.Body)).FirstOrDefault(-1);
                if (target >= 0) SetMoveBody(mv, target);
                break;
        }
        UpdateEdgeHighlight();
        BuildCommandPanel();
        CommandPanel.IsVisible = true;
        SchedulePreview(0);
    }

    void InitProfileSelection(string sketchId, List<int>? profiles)
    {
        var sk = !string.IsNullOrEmpty(sketchId) ? sketchId :
            _doc.Features.Take(_cmdEditIndex >= 0 ? _cmdEditIndex : _doc.ActiveCount).OfType<SketchFeature>().LastOrDefault(s => !s.Suppressed)?.Id;
        _cmdSketchId = sk;
        if (sk != null && _cmdBase != null && _cmdBase.Profiles.TryGetValue(sk, out var profs))
        {
            if (profiles != null) foreach (var p in profiles) Scene.SelectedProfiles.Add(p);
            else if (profs.Count == 1) Scene.SelectedProfiles.Add(0);
        }
        _doc.HiddenSketches.Remove(sk ?? "");
    }

    // ------------------------------------------------------------------ 패널 구성

    int _row;

    void BuildCommandPanel()
    {
        var f = _cmdFeature!;
        CommandTitle.Text = KindTitle(f.Kind) + (_cmdEditIndex >= 0 ? " 편집" : "");
        CommandIcon.Content = Icons.Make(f.Kind == "Measure" ? "measure" : Icons.ForFeature(f.Kind), 18);
        CommandRows.Children.Clear();
        CommandRows.RowDefinitions.Clear();
        _row = 0;
        _selLabel = null;
        CommandOk.IsVisible = f is not MeasureFeature;
        CommandCancel.Content = f is MeasureFeature ? "닫기" : "취소";
        switch (f)
        {
            case ExtrudeFeature ex:
                TypeRow("돌출", "얇은 돌출");
                _selLabel = SelectionRow("프로파일");
                Combo("시작", [("profile", "프로파일 평면"), ("offset", "간격띄우기")], ex.Offset == 0 ? "profile" : "offset", v => { if (v == "profile") ex.Offset = 0; else if (ex.Offset == 0) ex.Offset = 5; BuildCommandPanel(); });
                if (ex.Offset != 0) Number("간격띄우기", ex.Offset, v => ex.Offset = v, "mm");
                Combo("방향", [("one", "한쪽 방향"), ("two", "양쪽 방향"), ("symmetric", "대칭")], ex.Direction, v => { ex.Direction = v; BuildCommandPanel(); });
                Combo("범위 유형", [("distance", "거리"), ("all", "전체")], ex.Extent, v => { ex.Extent = v; BuildCommandPanel(); });
                if (ex.Extent == "distance")
                {
                    Number(ex.Direction == "two" ? "거리 1" : "거리", ex.Distance, v => { ex.Distance = v; AutoOperation(ex, v); }, "mm");
                    if (ex.Direction == "two") Number("거리 2", ex.Distance2, v => ex.Distance2 = v, "mm");
                }
                else Combo("방향 뒤집기", [("pos", "정방향"), ("neg", "반대 방향")], ex.Distance < 0 ? "neg" : "pos", v => ex.Distance = Math.Abs(ex.Distance) * (v == "neg" ? -1 : 1));
                DisabledNumber("테이퍼 각도", "0.0 deg", "테이퍼는 다음 단계에서 구현됩니다");
                OperationRows(ex);
                break;
            case RevolveFeature rv:
                _selLabel = SelectionRow("프로파일");
                var axes = new List<(string, string)> { ("x", "스케치 X축"), ("y", "스케치 Y축") };
                if (_doc.Find(_cmdSketchId ?? "") is SketchFeature sk)
                    for (int i = 0; i < sk.Entities.Count; i++)
                        if (sk.Entities[i] is LineEntity or ConstructionEntity) axes.Add(($"line:{i}", $"{(sk.Entities[i] is ConstructionEntity ? "구성선" : "선")} #{i + 1}"));
                var cur = rv.Axis.Kind == "line" ? $"line:{rv.Axis.Entity}" : rv.Axis.Kind;
                Combo("축", axes.ToArray(), cur, v => rv.Axis = v.StartsWith("line:") ? new Axis { Kind = "line", Entity = int.Parse(v[5..]) } : new Axis { Kind = v });
                Combo("유형", [("angle", "각도"), ("full", "전체")], rv.Angle >= 360 ? "full" : "angle", v => { rv.Angle = v == "full" ? 360 : 180; BuildCommandPanel(); });
                if (rv.Angle < 360) Number("각도", rv.Angle, v => rv.Angle = v, "deg");
                OperationRows(rv);
                break;
            case FilletFeature fl:
                TypeRow("모깎기", "규칙 모깎기");
                _selLabel = SelectionRow("모서리/면/피처");
                Combo("반지름 유형", [("constant", "상수")], "constant", _ => { });
                Number("반지름", fl.Radius, v => fl.Radius = v, "mm");
                CheckRow("접선 체인", true, enabled: false);
                break;
            case ChamferFeature ch:
                _selLabel = SelectionRow("모서리/면/피처");
                Combo("모따기 유형", [("equal", "동일 거리")], "equal", _ => { });
                Number("거리", ch.Distance, v => ch.Distance = v, "mm");
                CheckRow("접선 체인", true, enabled: false);
                break;
            case PressPullFeature pp:
                _selLabel = SelectionRow("면");
                Number("거리", pp.Distance, v => pp.Distance = v, "mm");
                Combo("간격띄우기 유형", [("new", "새 간격띄우기")], "new", _ => { });
                break;
            case HoleFeature ho:
                Combo("배치", [("face", "면 위 단일 구멍")], "face", _ => { });
                _selLabel = SelectionRow("면/점");
                Combo("범위", [("distance", "거리"), ("all", "전체")], ho.Through ? "all" : "distance", v => { ho.Through = v == "all"; BuildCommandPanel(); });
                Combo("구멍 유형", [("simple", "단순")], "simple", _ => { });
                Number("지름", ho.Diameter, v => ho.Diameter = v, "mm");
                if (!ho.Through) Number("깊이", ho.Depth, v => ho.Depth = v, "mm");
                break;
            case ShellFeature sh:
                _selLabel = SelectionRow("면/바디");
                Number("내부 두께", sh.Thickness, v => sh.Thickness = v, "mm");
                Combo("방향", [("inside", "내부")], "inside", _ => { });
                break;
            case MoveFeature mv:
                Combo("이동 객체", [("bodies", "바디")], "bodies", _ => { });
                _selLabel = SelectionRow("선택");
                Combo("이동 유형", [("translate", "변환")], "translate", _ => { });
                Number("X 거리", mv.Translate[0], v => mv.Translate[0] = v, "mm");
                Number("Y 거리", mv.Translate[1], v => mv.Translate[1] = v, "mm");
                Number("Z 거리", mv.Translate[2], v => mv.Translate[2] = v, "mm");
                var ax = mv.Axis[0] != 0 ? "x" : mv.Axis[1] != 0 ? "y" : "z";
                Combo("회전 축", [("z", "Z"), ("y", "Y"), ("x", "X")], ax, v => mv.Axis = v switch { "x" => [1, 0, 0], "y" => [0, 1, 0], _ => [0, 0, 1] });
                Number("각도", mv.Angle, v => mv.Angle = v, "deg");
                CheckRow("사본 작성", mv.Copy, v => { mv.Copy = v; SchedulePreview(); });
                break;
            case MeasureFeature:
                _selLabel = SelectionRow("선택");
                foreach (var (label, value) in MeasureResults()) InfoRow(label, value);
                break;
            case BoxFeature bx:
                Vec3("원점", bx.Origin);
                Vec3("길이·너비·높이", bx.Size);
                OperationRows(bx);
                break;
            case CylinderFeature cy:
                Vec3("중심", cy.Origin);
                var cax = cy.Axis[0] != 0 ? "x" : cy.Axis[1] != 0 ? "y" : "z";
                Combo("축", [("z", "Z"), ("y", "Y"), ("x", "X")], cax, v => cy.Axis = v switch { "x" => [1, 0, 0], "y" => [0, 1, 0], _ => [0, 0, 1] });
                Number("지름", cy.Radius * 2, v => cy.Radius = v / 2, "mm");
                Number("높이", cy.Height, v => cy.Height = v, "mm");
                OperationRows(cy);
                break;
            case SphereFeature sp:
                Vec3("중심", sp.Center);
                Number("지름", sp.Radius * 2, v => sp.Radius = v / 2, "mm");
                OperationRows(sp);
                break;
        }
        UpdateSelectionLabel();
        UpdateManipulator();
    }

    void AddRow(string label, Control c)
    {
        CommandRows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var l = new TextBlock { Text = label, Classes = { "rowlabel" } };
        Grid.SetRow(l, _row); Grid.SetColumn(l, 0);
        Grid.SetRow(c, _row); Grid.SetColumn(c, 1);
        CommandRows.Children.Add(l);
        CommandRows.Children.Add(c);
        _row++;
    }

    void TypeRow(string a, string b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var b1 = new Border { Child = new TextBlock { Text = a, FontSize = 11 }, Padding = new Thickness(8, 3), BorderBrush = new SolidColorBrush(Color.Parse("#0696D7")), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.Parse("#E4F2FA")) };
        var b2 = new Border { Child = new TextBlock { Text = b, FontSize = 11, Foreground = Brushes.Gray }, Padding = new Thickness(8, 3), BorderBrush = new SolidColorBrush(Color.Parse("#C9CDD3")), BorderThickness = new Thickness(1), [ToolTip.TipProperty] = "다음 단계에서 구현" };
        sp.Children.Add(b1); sp.Children.Add(b2);
        AddRow("유형", sp);
    }

    TextBlock SelectionRow(string label)
    {
        var tb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var clear = new Button { Content = "✕", FontSize = 9, Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), [DockPanel.DockProperty] = Dock.Right };
        clear.Click += (_, _) => { ClearCommandSelection(); };
        var dp = new DockPanel { Children = { clear, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { Text = "▶", FontSize = 9, Foreground = new SolidColorBrush(Color.Parse("#0696D7")), VerticalAlignment = VerticalAlignment.Center }, tb } } } };
        var b = new Border { Child = dp, Background = new SolidColorBrush(Color.Parse("#E4F2FA")), BorderBrush = new SolidColorBrush(Color.Parse("#0696D7")), BorderThickness = new Thickness(1), Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(2) };
        AddRow(label, b);
        return tb;
    }

    void ClearCommandSelection()
    {
        Scene.SelectedProfiles.Clear(); _cmdEdges.Clear(); _cmdFaces.Clear(); _measurePicks.Clear();
        UpdateEdgeHighlight();
        if (_cmdFeature is MeasureFeature) BuildCommandPanel(); else UpdateSelectionLabel();
        SchedulePreview();
    }

    readonly Dictionary<string, NumericUpDown> _numbers = [];

    void Number(string label, double value, Action<double> set, string unit)
    {
        var n = new NumericUpDown { Value = (decimal)value, Increment = unit == "deg" ? 15 : 1, FormatString = "0.###", FontSize = 12, MinHeight = 28, Minimum = -100000, Maximum = 100000 };
        n.ValueChanged += (_, e) => { if (e.NewValue is { } v) { set((double)v); SchedulePreview(); UpdateManipulator(); } };
        _numbers[label] = n;
        var row = new DockPanel();
        row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Brushes.Gray, [DockPanel.DockProperty] = Dock.Right });
        row.Children.Add(n);
        AddRow(label, row);
    }

    void DisabledNumber(string label, string text, string tip) =>
        AddRow(label, new TextBox { Text = text, IsEnabled = false, FontSize = 12, [ToolTip.TipProperty] = tip });

    void InfoRow(string label, string value) =>
        AddRow(label, new SelectableTextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

    void CheckRow(string label, bool value, Action<bool>? set = null, bool enabled = true)
    {
        var c = new CheckBox { IsChecked = value, IsEnabled = enabled };
        if (set != null) c.IsCheckedChanged += (_, _) => set(c.IsChecked == true);
        AddRow(label, c);
    }

    void Vec3(string label, double[] v)
    {
        var grid = new UniformGrid { Columns = 3 };
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            var n = new NumericUpDown { Value = (decimal)v[i], Increment = 1, FormatString = "0.###", FontSize = 11, ShowButtonSpinner = false, Margin = new Thickness(0, 0, 2, 0), Minimum = -100000, Maximum = 100000 };
            n.ValueChanged += (_, e) => { if (e.NewValue is { } x) { v[k] = (double)x; SchedulePreview(); } };
            grid.Children.Add(n);
        }
        AddRow(label, grid);
    }

    void Combo(string label, (string key, string text)[] items, string current, Action<string> set)
    {
        var c = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, text) in items) c.Items.Add(text);
        c.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.key == current));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(items[c.SelectedIndex].key); SchedulePreview(); UpdateManipulator(); } };
        AddRow(label, c);
    }

    void OperationRows(IBodyOp op)
    {
        Combo("작업", Ops, op.Operation, v => { op.Operation = v; _opAuto = false; BuildCommandPanel(); });
        if (op.Operation != "new" && _cmdBase != null && _cmdBase.Bodies.Count > 1)
        {
            var items = new List<(string, string)> { ("", "자동 (마지막 바디)") };
            items.AddRange(_cmdBase.Bodies.Select(b => (b.Index.ToString(), b.Name)));
            Combo("대상 바디", items.ToArray(), op.Target?.ToString() ?? "", v => op.Target = v == "" ? null : int.Parse(v));
        }
    }

    /// Fusion: 거리를 반대로 끌면 결합 ↔ 잘라내기 자동 전환 (사용자가 작업을 직접 고르기 전까지)
    void AutoOperation(IBodyOp op, double distance)
    {
        if (!_opAuto || _cmdBase == null || _cmdBase.Bodies.Count == 0) return;
        var want = distance < 0 ? "cut" : "join";
        if (op.Operation != want && op.Operation is "join" or "cut")
        {
            op.Operation = want;
            Dispatcher.UIThread.Post(BuildCommandPanel);
        }
    }

    void UpdateSelectionLabel()
    {
        if (_selLabel == null) return;
        _selLabel.Text = _cmdFeature switch
        {
            ExtrudeFeature or RevolveFeature => Scene.SelectedProfiles.Count == 0 ? "프로파일 선택" : $"{Scene.SelectedProfiles.Count}개 선택됨",
            FilletFeature or ChamferFeature => _cmdEdges.Count == 0 ? "모서리 선택" : $"{_cmdEdges.Count}개 선택됨",
            PressPullFeature => _cmdFaces.Count == 0 ? "평평한 면 선택" : "1개 선택됨",
            ShellFeature => _cmdFaces.Count == 0 ? "열 면 선택" : $"{_cmdFaces.Count}개 선택됨",
            HoleFeature h => _cmdFaces.Count == 0 && h.Origin.All(x => x == 0) && _cmdEditIndex < 0 ? "면 위의 점 클릭" : "1개 선택됨",
            MoveFeature => _cmdFaces.Count == 0 && _moveBody < 0 ? "바디 선택" : "1개 선택됨",
            MeasureFeature => _measurePicks.Count == 0 ? "모서리·면·점 선택" : $"{_measurePicks.Count}개 선택됨",
            _ => "",
        };
    }

    // ------------------------------------------------------------------ 선택

    bool PicksProfiles => _cmdFeature is ExtrudeFeature or RevolveFeature;
    bool PicksEdges => _cmdFeature is FilletFeature or ChamferFeature;
    bool PicksFaces => _cmdFeature is PressPullFeature or ShellFeature or HoleFeature or MoveFeature or MeasureFeature;

    (string sketch, int profile)? PickAnyProfile(float x, float y)
    {
        var (o, d) = Cam.Ray(x, y);
        (string, int)? best = null; float bestT = float.MaxValue;
        foreach (var sv in Scene.Sketches.Where(s => s.Visible || s.Active))
        {
            var f = new Frame(sv.Sketch.Plane);
            float den = Vector3.Dot(d, f.N);
            if (MathF.Abs(den) < 1e-6f) continue;
            float t = Vector3.Dot(f.O - o, f.N) / den;
            if (t <= 0 || t >= bestT) continue;
            var p = Picking.PickProfile(sv, f.To2(o + d * t));
            if (p != null) { best = (sv.Sketch.Id, p.Value); bestT = t; }
        }
        return best;
    }

    Scene BaseScene() => new() { Bodies = _cmdBase?.Bodies ?? [], HiddenBodies = Scene.HiddenBodies };

    bool IsBasePlanar(FaceRef f)
    {
        var b = (_cmdBase?.Bodies ?? Scene.Bodies).FirstOrDefault(b => b.Index == f.Body);
        return b != null && f.Face * 7 < b.FaceInfo.Length && b.FaceInfo[f.Face * 7] > 0.5f;
    }

    (Vector3 c, Vector3 n) BaseFace(FaceRef f)
    {
        var b = (_cmdBase?.Bodies ?? Scene.Bodies).First(b => b.Index == f.Body);
        int i = f.Face * 7;
        return (new Vector3(b.FaceInfo[i + 1], b.FaceInfo[i + 2], b.FaceInfo[i + 3]), Vector3.Normalize(new Vector3(b.FaceInfo[i + 4], b.FaceInfo[i + 5], b.FaceInfo[i + 6])));
    }

    void CommandHover(float x, float y)
    {
        if (Scene.Manipulator is { } mp)
        {
            bool hot = ManipulatorHit(x, y);
            if (hot != mp.hot) Scene.Manipulator = mp with { hot = hot };
        }
        if (PicksProfiles)
        {
            var hit = PickAnyProfile(x, y);
            Scene.HoverProfile = hit != null && hit.Value.sketch == _cmdSketchId ? hit.Value.profile : null;
        }
        else if (PicksEdges || _cmdFeature is MeasureFeature)
        {
            _cmdHoverEdge = Picking.PickEdge(BaseScene(), Cam, x, y);
            _cmdHoverFace = _cmdHoverEdge == null && _cmdFeature is MeasureFeature ? Picking.PickFace(BaseScene(), Cam, x, y)?.face : null;
            UpdateEdgeHighlight();
        }
        else if (PicksFaces)
        {
            var hit = Picking.PickFace(BaseScene(), Cam, x, y)?.face;
            _cmdHoverFace = hit is { } h && (_cmdFeature is not (PressPullFeature or HoleFeature) || IsBasePlanar(h)) ? h : null;
            UpdateEdgeHighlight();
        }
    }

    async Task CommandClick(float x, float y, bool add)
    {
        if (PicksProfiles)
        {
            var hit = PickAnyProfile(x, y);
            if (hit == null) return;
            if (hit.Value.sketch != _cmdSketchId)
            {
                _cmdSketchId = hit.Value.sketch;
                Scene.SelectedProfiles.Clear();
                foreach (var sv in Scene.Sketches) sv.Active = sv.Sketch.Id == _cmdSketchId;
                if (_cmdFeature is RevolveFeature) BuildCommandPanel();
            }
            Toggle(Scene.SelectedProfiles, hit.Value.profile);
        }
        else if (PicksEdges)
        {
            var e = Picking.PickEdge(BaseScene(), Cam, x, y);
            if (e is not { } er) return;
            if (_cmdEdges.Count > 0 && _cmdEdges.First().Body != er.Body) _cmdEdges.Clear();
            Toggle(_cmdEdges, er);
        }
        else if (_cmdFeature is MeasureFeature)
        {
            var e = Picking.PickEdge(BaseScene(), Cam, x, y);
            var hit = Picking.PickFace(BaseScene(), Cam, x, y);
            if (!add && _measurePicks.Count >= 2) { _measurePicks.Clear(); _cmdEdges.Clear(); _cmdFaces.Clear(); }
            if (e is { } er) { _cmdEdges.Add(er); _measurePicks.Add((EdgeMid(er), $"edge:{er.Body}:{er.Edge}")); }
            else if (hit is { } h) { _cmdFaces.Add(h.face); _measurePicks.Add((h.point, $"face:{h.face.Body}:{h.face.Face}")); }
            else return;
            BuildCommandPanel();
        }
        else if (PicksFaces)
        {
            var hit = Picking.PickFace(BaseScene(), Cam, x, y);
            if (hit is not { } h) return;
            switch (_cmdFeature)
            {
                case PressPullFeature when IsBasePlanar(h.face): _cmdFaces.Clear(); _cmdFaces.Add(h.face); break;
                case PressPullFeature: ShowToast("누르기/당기기: 평평한 면을 선택하십시오."); return;
                case ShellFeature:
                    if (_cmdFaces.Count > 0 && _cmdFaces.First().Body != h.face.Body) _cmdFaces.Clear();
                    Toggle(_cmdFaces, h.face);
                    break;
                case HoleFeature when IsBasePlanar(h.face): SetHoleFromFace(h.face, h.point); BuildCommandPanel(); break;
                case HoleFeature: ShowToast("구멍: 평평한 면을 클릭하십시오."); return;
                case MoveFeature mv: SetMoveBody(mv, h.face.Body); break;
            }
        }
        UpdateEdgeHighlight();
        UpdateSelectionLabel();
        UpdateManipulator();
        SchedulePreview();
        await Task.CompletedTask;
    }

    void SetHoleFromFace(FaceRef f, Vector3? point)
    {
        if (_cmdFeature is not HoleFeature ho) return;
        var (c, n) = BaseFace(f);
        var p = point ?? c;
        ho.Origin = [Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4)];
        ho.Normal = [n.X, n.Y, n.Z];
        ho.Body = f.Body;
        _cmdFaces.Clear(); _cmdFaces.Add(f);
    }

    int _moveBody = -1;

    void SetMoveBody(MoveFeature mv, int body)
    {
        _moveBody = body;
        mv.Body = body;
        var b = (_cmdBase?.Bodies ?? Scene.Bodies).FirstOrDefault(x => x.Index == body);
        if (b != null) mv.Pivot = [(b.BBox[0] + b.BBox[3]) / 2, (b.BBox[1] + b.BBox[4]) / 2, (b.BBox[2] + b.BBox[5]) / 2];
        _cmdFaces.Clear();
        Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(body);
    }

    Vector3 EdgeMid(EdgeRef er)
    {
        var b = _cmdBase!.Bodies.First(x => x.Index == er.Body);
        var pts = new List<Vector3>();
        for (int k = 0; k < b.EdgeIds.Length; k++)
            if (b.EdgeIds[k] == er.Edge) pts.Add(new Vector3(b.Edges[k * 6], b.Edges[k * 6 + 1], b.Edges[k * 6 + 2]));
        return pts.Count == 0 ? Vector3.Zero : pts[pts.Count / 2];
    }

    /// 기준 모델의 모서리/면으로 하이라이트 (미리보기 형상과 번호가 다르므로)
    void UpdateEdgeHighlight()
    {
        Scene.SelectedEdges.Clear(); Scene.HoverEdge = null;
        var segs = new List<float>(); var hov = new List<float>();
        foreach (var b in _cmdBase?.Bodies ?? [])
            for (int k = 0; k < b.EdgeIds.Length; k++)
            {
                var er = new EdgeRef(b.Index, (int)b.EdgeIds[k]);
                var target = _cmdEdges.Contains(er) ? segs : _cmdHoverEdge == er ? hov : null;
                if (target != null) for (int j = 0; j < 6; j++) target.Add(b.Edges[k * 6 + j]);
            }
        Scene.ExtraSelected = segs.ToArray();
        Scene.ExtraHover = hov.ToArray();
        // 면 선택은 기준 모델 = 현재 표시 모델일 때만 면 하이라이트로 그린다 (미리보기 전)
        Scene.SelectedFaces = _cmdFaces.ToHashSet();
        Scene.HoverFace = _cmdHoverFace;
        Viewport.Invalidate();
    }

    // ------------------------------------------------------------------ 측정 (I)

    IEnumerable<(string, string)> MeasureResults()
    {
        if (_cmdBase == null || _measurePicks.Count == 0) { yield return ("결과", "모서리·면을 클릭하십시오. 두 개를 고르면 거리를 표시합니다."); yield break; }
        foreach (var e in _cmdEdges.Take(2))
        {
            var b = _cmdBase.Bodies.First(x => x.Index == e.Body);
            double len = 0;
            for (int k = 0; k < b.EdgeIds.Length; k++)
                if (b.EdgeIds[k] == e.Edge)
                    len += Vector3.Distance(new Vector3(b.Edges[k * 6], b.Edges[k * 6 + 1], b.Edges[k * 6 + 2]), new Vector3(b.Edges[k * 6 + 3], b.Edges[k * 6 + 4], b.Edges[k * 6 + 5]));
            yield return ($"모서리 길이", $"{len:0.000} mm");
        }
        foreach (var f in _cmdFaces.Take(2))
        {
            var b = _cmdBase.Bodies.First(x => x.Index == f.Body);
            double area = 0;
            for (uint t = b.FaceStart[f.Face]; t < b.FaceStart[f.Face + 1]; t++)
            {
                Vector3 V(int k) { int i = (int)b.Indices[t * 3 + k] * 3; return new Vector3(b.Positions[i], b.Positions[i + 1], b.Positions[i + 2]); }
                area += Vector3.Cross(V(1) - V(0), V(2) - V(0)).Length() / 2;
            }
            yield return ("면 면적", $"{area:0.000} mm²");
        }
        if (_measurePicks.Count >= 2)
        {
            var a = _measurePicks[^2].p; var c = _measurePicks[^1].p;
            yield return ("거리 (선택 점 간)", $"{Vector3.Distance(a, c):0.000} mm");
            yield return ("ΔX / ΔY / ΔZ", $"{c.X - a.X:0.###} / {c.Y - a.Y:0.###} / {c.Z - a.Z:0.###}");
        }
        var bodyIdx = _measurePicks[^1].what.Split(':')[1];
        if (int.TryParse(bodyIdx, out var bi) && _cmdBase.Bodies.FirstOrDefault(x => x.Index == bi) is { } bb)
            yield return ("바디 크기", $"{bb.BBox[3] - bb.BBox[0]:0.##} × {bb.BBox[4] - bb.BBox[1]:0.##} × {bb.BBox[5] - bb.BBox[2]:0.##} mm");
    }

    // ------------------------------------------------------------------ 캔버스 조작기 (돌출/면 간격띄우기 거리 화살표)

    void UpdateManipulator()
    {
        Scene.Manipulator = null;
        ManipBox.IsVisible = false;
        if (_mode != Mode.Command || _cmdFeature == null || _cmdBase == null) return;
        Vector3 o, dir; float len;
        switch (_cmdFeature)
        {
            case ExtrudeFeature ex when ex.Extent == "distance" && _cmdSketchId != null && Scene.SelectedProfiles.Count > 0
                                        && _doc.Find(_cmdSketchId) is SketchFeature sk && _cmdBase.Profiles.TryGetValue(_cmdSketchId, out var profs):
            {
                var f = new Frame(sk.Plane);
                var p = profs[Scene.SelectedProfiles.First()];
                var c2 = new Vector2((float)p.Outer.Average(q => q[0]), (float)p.Outer.Average(q => q[1]));
                o = f.To3(c2.X, c2.Y) + f.N * (float)ex.Offset;
                dir = f.N; len = (float)ex.Distance;
                break;
            }
            case PressPullFeature pp when _cmdFaces.Count == 1:
            {
                var (c, n) = BaseFace(_cmdFaces.First());
                o = c; dir = n; len = (float)pp.Distance;
                break;
            }
            default: return;
        }
        Scene.Manipulator = (o, dir, len, false);
        PlaceManipBox();
        Viewport.Invalidate();
    }

    void PlaceManipBox()
    {
        if (Scene.Manipulator is not { } mp) return;
        var tip = Cam.Project(mp.origin + mp.dir * mp.length);
        if (float.IsNaN(tip.X)) return;
        ManipBox.Margin = new Thickness(tip.X + 18, tip.Y - 12, 0, 0);
        if (!ManipInput.IsFocused) ManipInput.Text = $"{mp.length:0.###} mm";
        ManipBox.IsVisible = true;
    }

    bool ManipulatorHit(float x, float y)
    {
        if (Scene.Manipulator is not { } mp) return false;
        var tip = Cam.Project(mp.origin + mp.dir * mp.length);
        var head = Cam.Project(mp.origin + mp.dir * (mp.length + Cam.PixelSize * 22 * MathF.Sign(mp.length == 0 ? 1 : mp.length)));
        var q = new Vector2(x, y);
        return SketchGeom.DistToSeg(q, tip, head) < 10;
    }

    /// 화살표를 끌면 화면에 투영된 방향으로의 이동량만큼 거리 변경
    void DragManipulator(float x, float y)
    {
        if (Scene.Manipulator is not { } mp) return;
        var a = Cam.Project(mp.origin);
        var b = Cam.Project(mp.origin + mp.dir);
        var axis = b - a;
        if (axis.LengthSquared() < 1e-6f) return;
        float perUnit = axis.Length();
        var start = new Vector2((float)_downPos.X, (float)_downPos.Y);
        float delta = Vector2.Dot(new Vector2(x, y) - start, Vector2.Normalize(axis)) / perUnit;
        double v = Math.Round(_manipStartValue + delta, SnapGrid.IsChecked == true ? 1 : 3);
        SetManipValue(v, rebuild: false);
    }

    void SetManipValue(double v, bool rebuild)
    {
        switch (_cmdFeature)
        {
            case ExtrudeFeature ex: ex.Distance = v; AutoOperation(ex, v); SyncNumber(ex.Direction == "two" ? "거리 1" : "거리", v); break;
            case PressPullFeature pp: pp.Distance = v; SyncNumber("거리", v); break;
        }
        if (Scene.Manipulator is { } mp) Scene.Manipulator = mp with { length = (float)v };
        PlaceManipBox();
        if (rebuild) BuildCommandPanel();
        SchedulePreview(60);
        Viewport.Invalidate();
    }

    void SyncNumber(string label, double v)
    {
        if (_numbers.TryGetValue(label, out var n) && n.Value != (decimal)v) n.Value = (decimal)v;
    }

    // ------------------------------------------------------------------ 미리보기 / 확인 / 취소

    void ApplySelectionToFeature()
    {
        switch (_cmdFeature)
        {
            case ExtrudeFeature ex: ex.Sketch = _cmdSketchId ?? ""; ex.Profiles = Scene.SelectedProfiles.OrderBy(i => i).ToList(); break;
            case RevolveFeature rv: rv.Sketch = _cmdSketchId ?? ""; rv.Profiles = Scene.SelectedProfiles.OrderBy(i => i).ToList(); break;
            case FilletFeature fl: fl.Body = _cmdEdges.FirstOrDefault().Body; fl.Edges = _cmdEdges.Select(e => e.Edge).OrderBy(i => i).ToList(); break;
            case ChamferFeature ch: ch.Body = _cmdEdges.FirstOrDefault().Body; ch.Edges = _cmdEdges.Select(e => e.Edge).OrderBy(i => i).ToList(); break;
            case PressPullFeature pp when _cmdFaces.Count > 0: pp.Body = _cmdFaces.First().Body; pp.Face = _cmdFaces.First().Face; break;
            case ShellFeature sh when _cmdFaces.Count > 0: sh.Body = _cmdFaces.First().Body; sh.Faces = _cmdFaces.Select(f => f.Face).OrderBy(i => i).ToList(); break;
        }
    }

    bool SelectionComplete => _cmdFeature switch
    {
        ExtrudeFeature or RevolveFeature => Scene.SelectedProfiles.Count > 0 && _cmdSketchId != null,
        FilletFeature or ChamferFeature => _cmdEdges.Count > 0,
        PressPullFeature => _cmdFaces.Count == 1,
        ShellFeature => _cmdFaces.Count > 0,
        HoleFeature => _cmdFaces.Count > 0 || _cmdEditIndex >= 0,
        MoveFeature => _moveBody >= 0 || _cmdEditIndex >= 0,
        MeasureFeature => false,
        _ => true,
    };

    Document PreviewDoc()
    {
        ApplySelectionToFeature();
        var d = _doc.Clone();
        var f = Document.FromJson(new Document { Features = [_cmdFeature!] }.ToJson()).Features[0];
        if (_cmdEditIndex >= 0) d.Features[_cmdEditIndex] = f;
        else
        {
            int at = d.ActiveCount;
            d.Features.Insert(at, f);
            if (d.Marker != null) d.Marker = at + 1;
        }
        return d;
    }

    void SchedulePreview(int ms = 150)
    {
        _previewTimer?.Stop();
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Max(1, ms)), DispatcherPriority.Background, async (_, _) =>
        {
            _previewTimer?.Stop();
            if (_mode != Mode.Command || _cmdFeature == null) return;
            if (!SelectionComplete)
            {
                var b = _doc.Clone(); b.Marker = _cmdEditIndex >= 0 ? _cmdEditIndex : _doc.ActiveCount;
                await Regenerate(b);
                return;
            }
            // 면을 고른 명령은 미리보기 형상의 면 번호가 달라지므로 면 하이라이트를 숨긴다
            await Regenerate(PreviewDoc());
            if (_cmdFeature is PressPullFeature or ShellFeature or HoleFeature) { Scene.SelectedFaces.Clear(); Scene.HoverFace = null; }
            PlaceManipBox();
        });
        _previewTimer.Start();
    }

    void UpdateCommandError()
    {
        if (_mode != Mode.Command || _cmdFeature == null || _last == null) { CommandError.IsVisible = false; return; }
        var st = _last.StatusOf(_cmdFeature.Id);
        CommandError.IsVisible = SelectionComplete && st is { Ok: false };
        CommandError.Text = st?.Error ?? "";
        UpdateSelectionLabel();
    }

    async Task OkCommand()
    {
        if (_mode != Mode.Command || _cmdFeature == null) return;
        if (_cmdFeature is MeasureFeature) { CancelCommand(); return; }
        if (!SelectionComplete)
        {
            ShowToast(_selLabel?.Text is { Length: > 0 } t ? $"{t} 하십시오." : "선택이 필요합니다.");
            return;
        }
        ApplySelectionToFeature();
        Snapshot();
        var f = _cmdFeature;
        if (_cmdEditIndex >= 0) _doc.Features[_cmdEditIndex] = f;
        else InsertFeature(f);
        // 사용한 스케치는 자동으로 숨김 (Fusion 기본 설정)
        if (f is ExtrudeFeature { Sketch: var s1 }) HideSketch(s1);
        if (f is RevolveFeature { Sketch: var s2 }) HideSketch(s2);
        CloseCommand();
        await Commit();
        var st = _last?.StatusOf(f.Id);
        if (st is { Ok: false }) ShowToast($"{f.Name}: {st.Error}", 8);
    }

    void CancelCommand(bool regen = true)
    {
        if (_mode != Mode.Command) return;
        CloseCommand();
        if (regen) _ = Regenerate();
    }

    void CloseCommand()
    {
        _previewTimer?.Stop();
        _mode = Mode.Idle;
        _cmd = null; _cmdFeature = null; _cmdEditIndex = -1; _cmdSketchId = null; _cmdBase = null;
        _cmdEdges.Clear(); _cmdFaces.Clear(); _measurePicks.Clear(); _cmdHoverEdge = null; _cmdHoverFace = null; _moveBody = -1;
        _numbers.Clear();
        Scene.ExtraSelected = []; Scene.ExtraHover = [];
        Scene.Manipulator = null;
        ManipBox.IsVisible = false;
        Scene.ClearSelection();
        CommandPanel.IsVisible = false;
        CommandError.IsVisible = false;
        CommandOk.IsVisible = true;
        CommandCancel.Content = "취소";
        Viewport.Focus();
    }
}

/// 측정은 문서에 남지 않는 명령 – 패널 구성용 표시 객체
public sealed class MeasureFeature : Feature
{
    public override string Kind => "Measure";
}
