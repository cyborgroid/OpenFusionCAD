using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFusionCAD.Kernel;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Sketching;
using OpenFusionCAD.Viewport;

namespace OpenFusionCAD.UI;

public partial class MainWindow
{
    string? _cmd;
    Feature? _cmdFeature;
    int _cmdEditIndex = -1;
    string? _cmdSketchId;
    RegenResult? _cmdBase;          // 이 피처를 적용하기 전 모델 (모서리 선택 기준)
    EdgeRef? _cmdHoverEdge;
    readonly HashSet<EdgeRef> _cmdEdges = [];
    DispatcherTimer? _previewTimer;
    TextBlock? _selLabel;

    static readonly (string key, string label)[] Ops = [("new", "새 바디"), ("join", "결합"), ("cut", "잘라내기"), ("intersect", "교차")];

    void HookCommandPanel()
    {
        CommandOk.Click += async (_, _) => await OkCommand();
        CommandCancel.Click += (_, _) => CancelCommand();
    }

    // ------------------------------------------------------------------ 시작 / 편집

    public async void StartCommand(string kind, Feature? edit = null)
    {
        if (_mode == Mode.Sketch) ExitSketch(false);
        if (_mode == Mode.PickSketchPlane) EndSketchPick();
        if (_mode == Mode.Command) CancelCommand(regen: false);
        _cmd = kind;
        _cmdEditIndex = edit != null ? _doc.Features.IndexOf(edit) : -1;
        _cmdFeature = edit != null ? Document.FromJson(new Document { Features = [edit] }.ToJson()).Features[0] : kind switch
        {
            "extrude" => new ExtrudeFeature(),
            "revolve" => new RevolveFeature(),
            "fillet" => new FilletFeature { Radius = 2 },
            "chamfer" => new ChamferFeature { Distance = 1 },
            "box" => new BoxFeature(),
            "cylinder" => new CylinderFeature(),
            "sphere" => new SphereFeature(),
            _ => null,
        };
        if (_cmdFeature == null) return;
        if (edit == null) _cmdFeature.Name = _doc.NextName(_cmdFeature.Kind);

        // 기준 모델: 이 피처 직전까지
        var baseDoc = _doc.Clone();
        baseDoc.Marker = _cmdEditIndex >= 0 ? _cmdEditIndex : _doc.ActiveCount;
        _cmdBase = await _kernel.RegenerateAsync(baseDoc);

        _cmdEdges.Clear();
        _cmdSketchId = null;
        Scene.ClearSelection();
        switch (_cmdFeature)
        {
            case ExtrudeFeature ex: InitProfileSelection(ex.Sketch, ex.Profiles); break;
            case RevolveFeature rv: InitProfileSelection(rv.Sketch, rv.Profiles); break;
            case FilletFeature fl when edit != null: foreach (var e in fl.Edges) _cmdEdges.Add(new EdgeRef(fl.Body, e)); break;
            case ChamferFeature ch when edit != null: foreach (var e in ch.Edges) _cmdEdges.Add(new EdgeRef(ch.Body, e)); break;
            case FilletFeature or ChamferFeature:
                // 미리 선택한 모서리 사용
                foreach (var e in Scene.SelectedEdges) _cmdEdges.Add(e);
                break;
        }
        _mode = Mode.Command;
        BuildCommandPanel();
        CommandPanel.IsVisible = true;
        SchedulePreview(0);
    }

    void InitProfileSelection(string sketchId, List<int>? profiles)
    {
        // 지정된 스케치가 없으면 마커 직전의 마지막 스케치
        var sk = !string.IsNullOrEmpty(sketchId) ? sketchId :
            _doc.Features.Take(_cmdEditIndex >= 0 ? _cmdEditIndex : _doc.ActiveCount).OfType<SketchFeature>().LastOrDefault(s => !s.Suppressed)?.Id;
        _cmdSketchId = sk;
        if (sk != null && _cmdBase != null && _cmdBase.Profiles.TryGetValue(sk, out var profs))
        {
            if (profiles != null) foreach (var p in profiles) Scene.SelectedProfiles.Add(p);
            else if (profs.Count == 1) Scene.SelectedProfiles.Add(0);
        }
        _hiddenSketches.Remove(sk ?? "");
    }

    // ------------------------------------------------------------------ 패널 구성

    int _row;

    void BuildCommandPanel()
    {
        var f = _cmdFeature!;
        CommandTitle.Text = f.Kind switch
        {
            "Extrude" => "돌출", "Revolve" => "회전", "Fillet" => "모깎기", "Chamfer" => "모따기",
            "Box" => "상자", "Cylinder" => "원통", "Sphere" => "구", _ => f.Kind,
        } + (_cmdEditIndex >= 0 ? " 편집" : "");
        CommandIcon.Content = Icons.Make(Icons.ForFeature(f.Kind), 18);
        CommandRows.Children.Clear();
        CommandRows.RowDefinitions.Clear();
        _row = 0;
        switch (f)
        {
            case ExtrudeFeature ex:
                _selLabel = SelectionRow("프로파일");
                Combo("방향", [("one", "한쪽 방향"), ("symmetric", "대칭"), ("two", "양쪽 방향")], ex.Direction, v => { ex.Direction = v; BuildCommandPanel(); });
                Number("거리", ex.Distance, v => ex.Distance = v, "mm");
                if (ex.Direction == "two") Number("거리 2", ex.Distance2, v => ex.Distance2 = v, "mm");
                OperationRows(ex);
                break;
            case RevolveFeature rv:
                _selLabel = SelectionRow("프로파일");
                var axes = new List<(string, string)> { ("x", "스케치 X축"), ("y", "스케치 Y축") };
                if (_doc.Find(_cmdSketchId ?? "") is SketchFeature sk)
                    for (int i = 0; i < sk.Entities.Count; i++)
                        if (sk.Entities[i] is LineEntity or ConstructionEntity) axes.Add(($"line:{i}", $"{(sk.Entities[i] is ConstructionEntity ? "작도선" : "선")} #{i + 1}"));
                var cur = rv.Axis.Kind == "line" ? $"line:{rv.Axis.Entity}" : rv.Axis.Kind;
                Combo("축", axes.ToArray(), cur, v => rv.Axis = v.StartsWith("line:") ? new Axis { Kind = "line", Entity = int.Parse(v[5..]) } : new Axis { Kind = v });
                Number("각도", rv.Angle, v => rv.Angle = v, "°");
                OperationRows(rv);
                break;
            case FilletFeature fl:
                _selLabel = SelectionRow("모서리");
                Number("반지름", fl.Radius, v => fl.Radius = v, "mm");
                break;
            case ChamferFeature ch:
                _selLabel = SelectionRow("모서리");
                Number("거리", ch.Distance, v => ch.Distance = v, "mm");
                break;
            case BoxFeature bx:
                Vec3("원점", bx.Origin);
                Vec3("크기", bx.Size);
                OperationRows(bx);
                break;
            case CylinderFeature cy:
                Vec3("중심", cy.Origin);
                var ax = cy.Axis[0] != 0 ? "x" : cy.Axis[1] != 0 ? "y" : "z";
                Combo("축", [("z", "Z"), ("y", "Y"), ("x", "X")], ax, v => cy.Axis = v switch { "x" => [1, 0, 0], "y" => [0, 1, 0], _ => [0, 0, 1] });
                Number("반지름", cy.Radius, v => cy.Radius = v, "mm");
                Number("높이", cy.Height, v => cy.Height = v, "mm");
                OperationRows(cy);
                break;
            case SphereFeature sp:
                Vec3("중심", sp.Center);
                Number("반지름", sp.Radius, v => sp.Radius = v, "mm");
                OperationRows(sp);
                break;
        }
        UpdateSelectionLabel();
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

    TextBlock SelectionRow(string label)
    {
        var tb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Child = tb, Background = new SolidColorBrush(Color.Parse("#E4F2FA")), BorderBrush = new SolidColorBrush(Color.Parse("#0696D7")), BorderThickness = new Thickness(1), Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(2) };
        AddRow(label, b);
        return tb;
    }

    void Number(string label, double value, Action<double> set, string unit)
    {
        var n = new NumericUpDown { Value = (decimal)value, Increment = unit == "°" ? 15 : 1, FormatString = "0.###", FontSize = 12, MinHeight = 28, Minimum = -100000, Maximum = 100000 };
        n.ValueChanged += (_, e) => { if (e.NewValue is { } v) { set((double)v); SchedulePreview(); } };
        var row = new DockPanel();
        row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Brushes.Gray, [DockPanel.DockProperty] = Dock.Right });
        row.Children.Add(n);
        AddRow(label, row);
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
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(items[c.SelectedIndex].key); SchedulePreview(); } };
        AddRow(label, c);
    }

    void OperationRows(IBodyOp op)
    {
        Combo("작업", Ops, op.Operation, v => { op.Operation = v; BuildCommandPanel(); });
        if (op.Operation != "new" && _cmdBase != null && _cmdBase.Bodies.Count > 0)
        {
            var items = new List<(string, string)> { ("", "자동 (마지막 바디)") };
            items.AddRange(_cmdBase.Bodies.Select(b => (b.Index.ToString(), b.Name)));
            Combo("대상 바디", items.ToArray(), op.Target?.ToString() ?? "", v => op.Target = v == "" ? null : int.Parse(v));
        }
    }

    void UpdateSelectionLabel()
    {
        if (_selLabel == null) return;
        _selLabel.Text = _cmdFeature switch
        {
            ExtrudeFeature or RevolveFeature => Scene.SelectedProfiles.Count == 0 ? "프로파일을 클릭하십시오" : $"{Scene.SelectedProfiles.Count}개 선택됨",
            FilletFeature or ChamferFeature => _cmdEdges.Count == 0 ? "모서리를 클릭하십시오" : $"모서리 {_cmdEdges.Count}개",
            _ => "",
        };
    }

    // ------------------------------------------------------------------ 선택

    bool PicksProfiles => _cmdFeature is ExtrudeFeature or RevolveFeature;
    bool PicksEdges => _cmdFeature is FilletFeature or ChamferFeature;

    /// 보이는 스케치들 중 커서 아래 프로파일
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

    void CommandHover(float x, float y)
    {
        if (PicksProfiles)
        {
            var hit = PickAnyProfile(x, y);
            Scene.HoverProfile = hit != null && hit.Value.sketch == _cmdSketchId ? hit.Value.profile : null;
            if (hit != null && hit.Value.sketch != _cmdSketchId) Scene.HoverProfile = null;
        }
        else if (PicksEdges)
        {
            _cmdHoverEdge = Picking.PickEdge(BaseScene(), Cam, x, y);
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
                // 다른 스케치의 프로파일을 고르면 그 스케치로 전환
                _cmdSketchId = hit.Value.sketch;
                Scene.SelectedProfiles.Clear();
                foreach (var sv in Scene.Sketches) sv.Active = sv.Sketch.Id == _cmdSketchId;
                if (_cmdFeature is RevolveFeature) BuildCommandPanel();
            }
            Toggle(Scene.SelectedProfiles, hit.Value.profile);
            UpdateSelectionLabel();
            SchedulePreview();
        }
        else if (PicksEdges)
        {
            var e = Picking.PickEdge(BaseScene(), Cam, x, y);
            if (e is not { } er) return;
            // 한 피처의 모서리는 같은 바디에 있어야 한다
            if (_cmdEdges.Count > 0 && _cmdEdges.First().Body != er.Body) _cmdEdges.Clear();
            Toggle(_cmdEdges, er);
            UpdateSelectionLabel();
            UpdateEdgeHighlight();
            SchedulePreview();
        }
        await Task.CompletedTask;
    }

    /// 기준 모델의 모서리로 하이라이트를 그린다 (미리보기 형상과 모서리 번호가 다르므로)
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
        Viewport.Invalidate();
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
        }
    }

    bool SelectionComplete => _cmdFeature switch
    {
        ExtrudeFeature or RevolveFeature => Scene.SelectedProfiles.Count > 0 && _cmdSketchId != null,
        FilletFeature or ChamferFeature => _cmdEdges.Count > 0,
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
                // 선택 전에는 기준 모델 표시
                var b = _doc.Clone(); b.Marker = _cmdEditIndex >= 0 ? _cmdEditIndex : _doc.ActiveCount;
                await Regenerate(b);
                return;
            }
            await Regenerate(PreviewDoc());
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
        if (!SelectionComplete) { ShowToast(PicksProfiles ? "프로파일을 선택하십시오." : "모서리를 선택하십시오."); return; }
        ApplySelectionToFeature();
        Snapshot();
        var f = _cmdFeature;
        if (_cmdEditIndex >= 0) _doc.Features[_cmdEditIndex] = f;
        else InsertFeature(f);
        // 사용한 스케치는 자동으로 숨김
        if (f is ExtrudeFeature { Sketch: var s1 }) _hiddenSketches.Add(s1);
        if (f is RevolveFeature { Sketch: var s2 }) _hiddenSketches.Add(s2);
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
        _cmdEdges.Clear(); _cmdHoverEdge = null;
        Scene.ExtraSelected = []; Scene.ExtraHover = [];
        Scene.ClearSelection();
        CommandPanel.IsVisible = false;
        CommandError.IsVisible = false;
        Viewport.Focus();
    }
}
