using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenFusionCAD.UI;

/// Fusion 360 명령 하나 (한국어판 이름, 기본 단축키)
public sealed record CadCommand(string Id, string Name, string Icon, string? Key, Action? Run)
{
    public bool Implemented => Run != null;
}

/// 툴바 그룹: 툴바에 보이는 아이콘 + 그룹 이름 ▾ 드롭다운의 전체 목록
public sealed record CommandGroup(string Title, string[] Visible, string[] All);

/// <summary>
/// Fusion 360 디자인 작업공간의 명령 체계. 툴바·그룹 드롭다운·바로 가기 상자(S)·마킹 메뉴·
/// 키보드 단축키가 모두 이 표를 사용한다. Run 이 null 이면 아직 구현되지 않은 명령으로,
/// Fusion 과 같은 자리에 보이되 실행하면 안내만 표시한다.
/// </summary>
public partial class MainWindow
{
    Dictionary<string, CadCommand> _cmds = [];
    string? _lastCommand;

    void BuildCommands()
    {
        var list = new List<CadCommand>
        {
            // ---- 솔리드 › 작성
            new("newcomponent", "새 구성요소", "component", null, null),
            new("sketch", "스케치 작성", "sketch", null, StartSketchPick),
            new("form", "양식 작성", "form", null, null),
            new("extrude", "돌출", "extrude", "E", () => StartCommand("extrude")),
            new("revolve", "회전", "revolve", null, () => StartCommand("revolve")),
            new("sweep", "스윕", "generic", null, null),
            new("loft", "로프트", "generic", null, null),
            new("rib", "리브", "generic", null, null),
            new("web", "웹", "generic", null, null),
            new("emboss", "엠보싱", "generic", null, null),
            new("hole", "구멍", "hole", "H", () => StartCommand("hole")),
            new("thread", "스레드", "generic", null, null),
            new("box", "상자", "box", null, () => StartCommand("box")),
            new("cylinder", "원통", "cylinder", null, () => StartCommand("cylinder")),
            new("sphere", "구", "sphere", null, () => StartCommand("sphere")),
            new("torus", "원환", "generic", null, null),
            new("coil", "코일", "generic", null, null),
            new("pipe", "파이프", "generic", null, null),
            new("pattern", "직사각형 패턴", "pattern", null, null),
            new("circpattern3d", "원형 패턴", "circpattern", null, null),
            new("mirror3d", "대칭", "mirror", null, null),
            new("thicken", "두껍게", "generic", null, null),
            new("boundaryfill", "경계 채우기", "generic", null, null),
            // ---- 솔리드 › 자동화
            new("automodel", "자동화된 모델링", "generic", null, null),
            // ---- 솔리드 › 수정
            new("presspull", "누르기/당기기", "presspull", "Q", () => StartPressPull()),
            new("fillet", "모깎기", "fillet", "F", () => StartCommand("fillet")),
            new("chamfer", "모따기", "chamfer", null, () => StartCommand("chamfer")),
            new("shell", "쉘", "shell", null, () => StartCommand("shell")),
            new("draft", "구배", "generic", null, null),
            new("scale", "축척", "generic", null, null),
            new("combine", "결합", "combine", null, null),
            new("replaceface", "면 대체", "generic", null, null),
            new("splitface", "면 분할", "generic", null, null),
            new("splitbody", "바디 분할", "generic", null, null),
            new("move", "이동/복사", "move", "M", () => StartCommand("move")),
            new("align", "정렬", "generic", null, null),
            new("delete", "삭제", "trim", "Delete", () => _ = DeleteSelection()),
            new("physmaterial", "물리 재료", "generic", null, null),
            new("appearance", "모양", "appearance", "A", () => _ = ShowAppearance()),
            new("params", "매개변수 변경", "generic", null, null),
            new("computeall", "모두 계산", "generic", "Ctrl+B", () => _ = ComputeAll()),
            // ---- 솔리드 › 조립
            new("joint", "접합", "joint", "J", null),
            new("asbuilt", "같은 위치 접합", "joint", "Shift+J", null),
            new("jointorigin", "접합 원점", "generic", null, null),
            new("rigidgroup", "강체 그룹", "generic", null, null),
            // ---- 솔리드 › 구성
            new("offsetplane", "간격띄우기 평면", "offsetplane", null, null),
            new("angleplane", "각도 평면", "offsetplane", null, null),
            new("midplane", "중간 평면", "offsetplane", null, null),
            new("axisline", "선을 통과하는 축", "axis", null, null),
            new("point3d", "점", "point", null, null),
            // ---- 솔리드 › 검사
            new("measure", "측정", "measure", "I", () => StartCommand("measure")),
            new("interference", "간섭", "interference", null, null),
            new("section", "단면 분석", "section", null, null),
            new("centerofmass", "질량 중심", "generic", null, null),
            // ---- 솔리드 › 삽입
            new("insertcad", "CAD 파일 삽입 (STEP/IGES)", "insert", null, () => _ = InsertCad()),
            new("insertderive", "파생 삽입", "generic", null, null),
            new("decal", "데칼", "decal", null, null),
            new("canvas", "캔버스", "canvas", null, null),
            new("insertmesh", "메쉬 삽입", "generic", null, null),
            new("insertsvg", "SVG 삽입", "generic", null, null),
            new("insertdxf", "DXF 삽입", "generic", null, null),
            // ---- 선택
            new("select", "선택", "select", null, () => { if (_mode == Mode.Command) CancelCommand(); else if (_mode == Mode.Sketch) SetSketchTool(SketchTool.Select); }),
            new("windowselect", "창 선택", "window", "1", () => ShowToast("창 선택: 왼쪽→오른쪽으로 끌면 완전히 포함된 항목, 오른쪽→왼쪽은 걸친 항목까지 선택합니다.")),
            new("freeselect", "자유형 선택", "generic", "2", null),
            new("paintselect", "페인트 선택", "generic", "3", null),
            // ---- 기타 (바로 가기 상자/키보드)
            new("visibility", "가시성 전환", "bulb", "V", () => ToggleVisibility()),
            new("scripts", "스크립트 및 추가 기능", "extensions", "Shift+S", null),
            new("fit", "맞춤", "fit", "F6", () => FitView()),
            new("lookat", "다음 보기", "lookat", null, () => LookAtSelection()),
            new("undo", "명령 취소", "undo", "Ctrl+Z", () => _ = Undo()),
            new("redo", "명령 복구", "redo", "Ctrl+Y", () => _ = Redo()),
            new("save", "저장", "save", "Ctrl+S", () => _ = Save()),
            new("export", "내보내기...", "export", null, () => _ = ExportDialog()),
            // ---- 스케치 › 작성
            new("line", "선", "line", "L", () => SetSketchTool(SketchTool.Line)),
            new("rect2", "2점 직사각형", "rect", "R", () => SetSketchTool(SketchTool.Rect)),
            new("rect3", "3점 직사각형", "rect", null, null),
            new("rectcenter", "중심 직사각형", "rectcenter", null, () => SetSketchTool(SketchTool.CenterRect)),
            new("circle", "중심 지름 원", "circle", "C", () => SetSketchTool(SketchTool.Circle)),
            new("circle2", "2점 원", "circle", null, null),
            new("circle3", "3점 원", "circle", null, null),
            new("arc3", "3점 호", "arc", null, () => SetSketchTool(SketchTool.Arc)),
            new("arccenter", "중심점 호", "arc", null, null),
            new("arctangent", "접선 호", "arc", null, null),
            new("polygonc", "외접 폴리곤", "polygon", null, () => SetSketchTool(SketchTool.PolygonCircumscribed)),
            new("polygoni", "내접 폴리곤", "polygon", null, () => SetSketchTool(SketchTool.PolygonInscribed)),
            new("ellipse", "타원", "generic", null, null),
            new("slot", "슬롯", "generic", null, null),
            new("spline", "스플라인", "generic", null, null),
            new("point", "점", "point", null, () => SetSketchTool(SketchTool.Point)),
            new("text", "텍스트", "generic", null, null),
            new("skmirror", "대칭", "mirror", null, null),
            new("skcircpattern", "원형 패턴", "circpattern", null, null),
            new("skrectpattern", "직사각형 패턴", "pattern", null, null),
            new("project", "투영/포함", "project", "P", null),
            new("dimension", "스케치 치수", "dimension", "D", () => SetSketchTool(SketchTool.Dimension)),
            // ---- 스케치 › 수정
            new("skfillet", "모깎기", "sketchfillet", null, null),
            new("trim", "자르기", "trim", "T", null),
            new("extend", "연장", "generic", null, null),
            new("break", "끊기", "generic", null, null),
            new("skscale", "스케치 축척", "generic", null, null),
            new("skoffset", "간격띄우기", "offset", "O", null),
            new("construction", "보통/구성", "construction", "X", () => ToggleConstruction()),
            // ---- 스케치 › 구속조건
            new("c-horizontal", "수평/수직", "c-horizontal", null, null),
            new("c-coincident", "일치", "c-coincident", null, null),
            new("c-tangent", "접선", "c-tangent", null, null),
            new("c-equal", "동일", "c-equal", null, null),
            new("c-parallel", "평행", "c-parallel", null, null),
            new("c-perpendicular", "직각", "c-perpendicular", null, null),
            new("c-fix", "고정/고정 해제", "c-fix", null, null),
            new("c-midpoint", "중간점", "c-midpoint", null, null),
            new("c-concentric", "동심", "c-concentric", null, null),
            new("c-collinear", "동일선상", "c-collinear", null, null),
            new("finishsketch", "스케치 마침", "finish", null, () => ExitSketch(true)),
        };
        _cmds = list.ToDictionary(c => c.Id);
    }

    static readonly CommandGroup[] SolidGroups =
    [
        new("작성", ["newcomponent", "sketch", "form", "extrude", "revolve", "hole", "pattern"],
            ["newcomponent", "sketch", "form", "extrude", "revolve", "sweep", "loft", "rib", "web", "emboss", "hole", "thread", "box", "cylinder", "sphere", "torus", "coil", "pipe", "pattern", "circpattern3d", "mirror3d", "thicken", "boundaryfill"]),
        new("자동화", ["automodel"], ["automodel"]),
        new("수정", ["presspull", "fillet", "chamfer", "shell", "combine", "move"],
            ["presspull", "fillet", "chamfer", "shell", "draft", "scale", "combine", "replaceface", "splitface", "splitbody", "move", "align", "delete", "physmaterial", "appearance", "params", "computeall"]),
        new("조립", ["newcomponent", "joint", "asbuilt"], ["newcomponent", "joint", "asbuilt", "jointorigin", "rigidgroup"]),
        new("구성", ["offsetplane", "axisline"], ["offsetplane", "angleplane", "midplane", "axisline", "point3d"]),
        new("검사", ["measure", "interference", "section"], ["measure", "interference", "section", "centerofmass"]),
        new("삽입", ["insertcad", "decal", "canvas"], ["insertderive", "decal", "canvas", "insertmesh", "insertsvg", "insertdxf", "insertcad"]),
        new("선택", ["select"], ["select", "windowselect", "freeselect", "paintselect"]),
    ];

    static readonly CommandGroup[] SketchGroups =
    [
        new("작성", ["line", "rect2", "circle", "arc3", "polygonc", "point", "dimension"],
            ["line", "rect2", "rect3", "rectcenter", "circle", "circle2", "circle3", "arc3", "arccenter", "arctangent", "polygonc", "polygoni", "ellipse", "slot", "spline", "point", "text", "skmirror", "skcircpattern", "skrectpattern", "project", "dimension"]),
        new("수정", ["skfillet", "trim", "skoffset"], ["skfillet", "trim", "extend", "break", "skscale", "skoffset", "construction"]),
        new("구속조건", ["c-horizontal", "c-coincident", "c-tangent", "c-equal", "c-parallel", "c-perpendicular", "c-fix", "c-midpoint", "c-concentric", "c-collinear"],
            ["c-horizontal", "c-coincident", "c-tangent", "c-equal", "c-parallel", "c-perpendicular", "c-fix", "c-midpoint", "c-concentric", "c-collinear"]),
        new("검사", ["measure"], ["measure"]),
        new("삽입", ["canvas"], ["canvas", "insertdxf", "insertsvg"]),
        new("선택", ["select"], ["select", "windowselect", "freeselect", "paintselect"]),
    ];

    /// 명령 실행 (미구현이면 안내)
    void Run(string id)
    {
        if (!_cmds.TryGetValue(id, out var c)) return;
        if (c.Run == null)
        {
            ShowToast($"'{c.Name}' 은(는) 다음 단계에서 구현됩니다. (Fusion 과 같은 위치에 자리만 있습니다)");
            return;
        }
        if (id is not ("undo" or "redo" or "save" or "fit" or "select")) _lastCommand = id;
        c.Run();
    }

    string Tip(CadCommand c) => c.Key != null ? $"{c.Name}  ({c.Key})" : c.Name;

    // ------------------------------------------------------------------ 툴바

    string _tab = "solid";
    readonly Dictionary<SketchTool, Button> _sketchToolButtons = [];

    void BuildToolbar()
    {
        TabStrip.Children.Clear();
        var tabs = new List<(string key, string label)> { ("solid", "솔리드"), ("surface", "곡면"), ("mesh", "메쉬"), ("sheet", "판금"), ("plastic", "플라스틱"), ("utilities", "유틸리티"), ("manage", "관리") };
        if (_mode == Mode.Sketch) tabs.Add(("sketch", "스케치"));
        foreach (var (key, label) in tabs)
        {
            var b = new Button { Content = label, Classes = { "tab" } };
            var active = _mode == Mode.Sketch ? key == "sketch" : key == _tab;
            if (active) b.Classes.Add(key == "sketch" ? "sketch" : "active");
            b.Click += (_, _) =>
            {
                if (key == "sketch") return;
                if (_mode == Mode.Sketch) ExitSketch(true);
                if (key is "solid" or "utilities") { _tab = key; BuildToolbar(); }
                else ShowToast($"'{label}' 탭은 다음 단계에서 구현됩니다.");
            };
            TabStrip.Children.Add(b);
        }

        ToolGroups.Children.Clear();
        _sketchToolButtons.Clear();
        if (_mode == Mode.Sketch)
        {
            foreach (var g in SketchGroups) AddGroup(g);
            AddFinishSketchGroup();
            UpdateSketchToolButtons();
            return;
        }
        if (_tab == "utilities")
        {
            AddGroup(new("유틸리티", ["export", "insertcad"], ["export", "insertcad", "computeall"]));
            return;
        }
        foreach (var g in SolidGroups) AddGroup(g);
    }

    void AddGroup(CommandGroup g)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var id in g.Visible)
        {
            if (!_cmds.TryGetValue(id, out var c)) continue;
            var b = new Button { Classes = { "tool" }, Content = Icons.Make(c.Icon, 26) };
            if (!c.Implemented) b.Opacity = 0.45;
            ToolTip.SetTip(b, Tip(c) + (c.Implemented ? "" : "\n(다음 단계에서 구현)"));
            b.Click += (_, _) => { Run(id); Viewport.Focus(); };
            icons.Children.Add(b);
            var tool = id switch
            {
                "line" => SketchTool.Line, "rect2" => SketchTool.Rect, "circle" => SketchTool.Circle, "arc3" => SketchTool.Arc,
                "polygonc" => SketchTool.PolygonCircumscribed, "point" => SketchTool.Point, "dimension" => SketchTool.Dimension, _ => (SketchTool?)null,
            };
            if (tool is { } st) _sketchToolButtons[st] = b;
        }
        var label = new Button { Classes = { "group" }, Content = $"{g.Title} ▾" };
        var flyout = new MenuFlyout();
        foreach (var id in g.All)
        {
            if (!_cmds.TryGetValue(id, out var c)) continue;
            var mi = new MenuItem
            {
                Header = c.Name,
                Icon = Icons.Make(c.Icon, 16),
                InputGesture = c.Key is { } k && k.Length > 0 && !k.StartsWith("Delete") ? TryGesture(k) : null,
                Opacity = c.Implemented ? 1 : 0.5,
            };
            mi.Click += (_, _) => { Run(id); Viewport.Focus(); };
            flyout.Items.Add(mi);
        }
        label.Flyout = flyout;
        var group = new StackPanel { Spacing = 0, Margin = new Thickness(6, 0) };
        group.Children.Add(icons);
        group.Children.Add(label);
        ToolGroups.Children.Add(group);
        ToolGroups.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.Parse("#E1E4E8")), Margin = new Thickness(0, 4) });
    }

    void AddFinishSketchGroup()
    {
        var b = new Button { Classes = { "tool" }, Content = Icons.Make("finish", 26) };
        ToolTip.SetTip(b, "스케치 마침");
        b.Click += (_, _) => ExitSketch(true);
        var label = new Button { Classes = { "group" }, Content = "스케치 마침 ▾", Foreground = new SolidColorBrush(Color.Parse("#1E6B3D")) };
        var fl = new MenuFlyout();
        var mi = new MenuItem { Header = "스케치 마침" }; mi.Click += (_, _) => ExitSketch(true); fl.Items.Add(mi);
        label.Flyout = fl;
        var group = new StackPanel { Margin = new Thickness(6, 0) };
        group.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { b } });
        group.Children.Add(label);
        ToolGroups.Children.Add(group);
    }

    static KeyGesture? TryGesture(string k)
    {
        try { return KeyGesture.Parse(k); } catch { return null; }
    }

    // ------------------------------------------------------------------ 키보드 (Fusion 기본 단축키)

    static readonly Dictionary<Key, string> SolidKeys = new()
    {
        [Key.S] = "shortcutbox", [Key.Q] = "presspull", [Key.E] = "extrude", [Key.H] = "hole", [Key.F] = "fillet",
        [Key.M] = "move", [Key.I] = "measure", [Key.A] = "appearance", [Key.V] = "visibility", [Key.J] = "joint",
        [Key.D1] = "windowselect", [Key.D2] = "freeselect", [Key.D3] = "paintselect", [Key.F6] = "fit", [Key.Delete] = "delete",
    };

    static readonly Dictionary<Key, string> SketchKeys = new()
    {
        [Key.L] = "line", [Key.R] = "rect2", [Key.C] = "circle", [Key.D] = "dimension", [Key.X] = "construction",
        [Key.T] = "trim", [Key.O] = "skoffset", [Key.P] = "project",
        [Key.S] = "shortcutbox", [Key.E] = "extrude", [Key.Q] = "presspull", [Key.I] = "measure", [Key.F6] = "fit",
    };

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && e.Key != Key.Escape) return;
        if (_marking.IsVisible || ShortcutBox.IsVisible)
        {
            if (e.Key == Key.Escape) { HideMarkingMenu(); HideShortcutBox(); e.Handled = true; }
            return;
        }
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control), shift = mods.HasFlag(KeyModifiers.Shift);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: await Undo(); break;
                case Key.Y: await Redo(); break;
                case Key.S: await Save(shift); break;
                case Key.O: await OpenDialog(); break;
                case Key.N: await NewDocument(); break;
                case Key.B: await ComputeAll(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.S) { Run("scripts"); e.Handled = true; return; }
        if (shift && e.Key == Key.J) { Run("asbuilt"); e.Handled = true; return; }

        if (e.Key == Key.Escape)
        {
            if (_mode == Mode.Command) CancelCommand();
            else if (_mode == Mode.PickSketchPlane) EndSketchPick();
            else if (_mode == Mode.Sketch) SketchEscape();
            else { Scene.ClearSelection(); Viewport.Invalidate(); }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _mode == Mode.Command) { await OkCommand(); e.Handled = true; return; }

        if (_mode == Mode.Sketch)
        {
            if (SketchDigitKey(e)) { e.Handled = true; return; }
            if (e.Key is Key.Delete or Key.Back) { DeleteSelectedEntities(); e.Handled = true; return; }
            if (SketchKeys.TryGetValue(e.Key, out var sid))
            {
                if (sid == "shortcutbox") ShowShortcutBox();
                else if (sid is "extrude" or "presspull" or "measure") { ExitSketch(false); Run(sid); }
                else Run(sid);
                e.Handled = true;
            }
            return;
        }
        if (SolidKeys.TryGetValue(e.Key, out var id))
        {
            if (id == "shortcutbox") ShowShortcutBox();
            else if (id == "delete" && _selectedFeature != null && !HasCanvasSelection()) await DeleteFeature(_selectedFeature);
            else Run(id);
            e.Handled = true;
        }
    }

    bool HasCanvasSelection() => Scene.SelectedBodies.Count + Scene.SelectedFaces.Count + Scene.SelectedEdges.Count > 0;

    // ------------------------------------------------------------------ 바로 가기 상자 (S)

    List<CadCommand> _sbItems = [];

    void ShowShortcutBox()
    {
        HideMarkingMenu();
        ShortcutBox.Margin = new Thickness(Math.Clamp(_cursorPx.X - 20, 10, Math.Max(10, CanvasArea.Bounds.Width - 330)), Math.Clamp(_cursorPx.Y - 20, 10, Math.Max(10, CanvasArea.Bounds.Height - 340)), 0, 0);
        ShortcutBox.IsVisible = true;
        ShortcutSearch.Text = "";
        FillShortcutList("");
        ShortcutSearch.Focus();
    }

    void HideShortcutBox()
    {
        if (!ShortcutBox.IsVisible) return;
        ShortcutBox.IsVisible = false;
        Viewport.Focus();
    }

    void FillShortcutList(string q)
    {
        var sketch = _mode == Mode.Sketch;
        var pool = (sketch ? SketchGroups : SolidGroups).SelectMany(g => g.All).Concat(["undo", "redo", "save", "export", "fit", "lookat", "visibility"]).Distinct()
            .Where(_cmds.ContainsKey).Select(i => _cmds[i]);
        _sbItems = pool.Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Key ?? "").Equals(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Implemented).Take(40).ToList();
        ShortcutList.ItemsSource = _sbItems.Select(c =>
        {
            var row = new DockPanel();
            row.Children.Add(new TextBlock { Text = c.Key ?? "", FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right });
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = c.Implemented ? 1 : 0.5 };
            left.Children.Add(Icons.Make(c.Icon, 18));
            left.Children.Add(new TextBlock { Text = c.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(left);
            return row;
        }).ToList();
        ShortcutList.SelectedIndex = _sbItems.Count > 0 ? 0 : -1;
    }

    void HookShortcutBox()
    {
        ShortcutSearch.TextChanged += (_, _) => FillShortcutList(ShortcutSearch.Text ?? "");
        ShortcutSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { ShortcutList.SelectedIndex = Math.Min(_sbItems.Count - 1, ShortcutList.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { ShortcutList.SelectedIndex = Math.Max(0, ShortcutList.SelectedIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Enter) { RunShortcutSelection(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideShortcutBox(); e.Handled = true; }
        };
        ShortcutList.DoubleTapped += (_, _) => RunShortcutSelection();
    }

    void RunShortcutSelection()
    {
        var i = ShortcutList.SelectedIndex;
        HideShortcutBox();
        if (i >= 0 && i < _sbItems.Count) Run(_sbItems[i].Id);
    }

    // ------------------------------------------------------------------ 마킹 메뉴 (오른쪽 클릭)

    MarkingMenu _marking = null!;

    void ShowMarkingMenu(Point at)
    {
        HideShortcutBox();
        string repeat = _lastCommand != null && _cmds.TryGetValue(_lastCommand, out var lc) ? $"반복: {lc.Name}" : "반복";
        (string label, string icon, Action act)? Item(string id, string? label = null) =>
            _cmds.TryGetValue(id, out var c) ? (label ?? c.Name, c.Icon, () => Run(id)) : null;
        var repeatItem = (repeat, "redo", (Action)(() => { if (_lastCommand != null) Run(_lastCommand); }));
        (string, string, Action)?[] radial;
        List<(string, Action)> overflow;
        if (_mode == Mode.Sketch)
        {
            // 북쪽부터 시계 방향
            radial = [repeatItem, Item("line"), Item("rect2"), Item("circle"), Item("finishsketch"), Item("dimension"), Item("undo"), Item("construction")];
            overflow = [("다음 보기", () => LookAtSelection()), ("선택 항목 삭제", () => DeleteSelectedEntities()), ("3점 호", () => Run("arc3")), ("외접 폴리곤", () => Run("polygonc")), ("맞춤", FitView)];
        }
        else
        {
            radial = [repeatItem, Item("delete"), Item("presspull"), Item("move"), Item("sketch"), Item("hole"), Item("undo"), Item("redo")];
            overflow = [("스케치 작성", StartSketchPick), ("표시/숨기기", ToggleVisibility), ("검색기에서 찾기", FindInBrowser), ("다음 보기", () => LookAtSelection()),
                        ("측정", () => Run("measure")), ("모양", () => Run("appearance")), ("맞춤", FitView), ("모두 계산", () => Run("computeall"))];
        }
        _marking.Show(at, radial.Select(r => r is { } x ? x : ("", "generic", (Action)(() => { }))).ToArray(), overflow);
    }

    void HideMarkingMenu() => _marking.Hide();
}
