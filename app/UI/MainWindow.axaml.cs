using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenFusionCAD.Kernel;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Viewport;

namespace OpenFusionCAD.UI;

public enum Mode { Idle, PickSketchPlane, Sketch, Command }

public partial class MainWindow : Window
{
    readonly KernelClient _kernel = new();
    Document _doc = new();
    string? _path;
    bool _dirty;
    readonly List<string> _undo = [], _redo = [];
    RegenResult? _last;
    int _regenSeq;
    Mode _mode = Mode.Idle;
    string? _selectedFeature;
    Scene Scene => Viewport.Scene;
    Camera Cam => Viewport.Camera;

    public MainWindow() : this([]) { }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        BuildAppBar();
        BuildToolbar();
        BuildNavBar();
        BuildTimelineControls();
        Cube.Camera = Cam;
        Cube.ViewRequested += n => { Cam.LookFrom(n); Viewport.Invalidate(); };
        Cube.HomeRequested += () => { var (a, b) = Scene.Bounds(); Cam.Home(a, b); Viewport.Invalidate(); };
        Viewport.Rendered += () => { Cube.InvalidateVisual(); UpdateOverlay(); };
        HookViewportInput();
        HookCommandPanel();
        HookSketchPalette();
        HookCloud();
        KeyDown += OnKeyDown;
        Closing += (_, e) => { if (_dirty && !_confirmedClose) { e.Cancel = true; _ = ConfirmClose(); } };
        Closed += (_, _) => _kernel.Dispose();
        Opened += async (_, _) =>
        {
            Viewport.Focus();
            int t = Array.IndexOf(args, "--autotest");
            if (t >= 0 && t + 1 < args.Length) { await RunAutoTest(args[t + 1]); return; }
            var file = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
            if (file != null) await OpenPath(file);
            else await Regenerate();
            var (a, b) = Scene.Bounds();
            Cam.Home(a, b);
            Viewport.Invalidate();
        };
        UpdateTitle();
    }

    // ------------------------------------------------------------------ 상태 표시

    DispatcherTimer? _toastTimer;
    public void ShowToast(string msg, double seconds = 4)
    {
        ToastText.Text = msg;
        Toast.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) => { Toast.IsVisible = false; _toastTimer?.Stop(); });
        _toastTimer.Start();
    }

    void UpdateTitle()
    {
        var name = _path != null ? Path.GetFileNameWithoutExtension(_path) : "제목 없음";
        DocTitle.Text = name;
        DirtyMark.IsVisible = _dirty;
        Title = $"{name}{(_dirty ? " *" : "")} - OpenFusionCAD";
    }

    // ------------------------------------------------------------------ 문서 변경 / 실행 취소

    /// 문서를 바꾸기 전에 호출: 실행 취소 스냅숏 저장
    void Snapshot()
    {
        _undo.Add(_doc.ToJson());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    async Task Commit(string? toast = null)
    {
        _dirty = true;
        UpdateTitle();
        await Regenerate();
        if (toast != null) ShowToast(toast);
    }

    async Task Undo()
    {
        if (_mode == Mode.Command) CancelCommand();
        if (_undo.Count == 0) return;
        _redo.Add(_doc.ToJson());
        _doc = Document.FromJson(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        if (_mode == Mode.Sketch && _doc.Find(_sketchId ?? "") == null) ExitSketch(false);
        _dirty = true; UpdateTitle();
        await Regenerate();
    }

    async Task Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_doc.ToJson());
        _doc = Document.FromJson(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _dirty = true; UpdateTitle();
        await Regenerate();
    }

    // ------------------------------------------------------------------ 재생성

    /// 문서(또는 미리보기 문서)를 커널로 다시 계산하고 화면을 갱신한다.
    async Task Regenerate(Document? preview = null)
    {
        int seq = ++_regenSeq;
        var doc = preview ?? EffectiveDoc();
        RegenResult r;
        try { r = await _kernel.RegenerateAsync(doc); }
        catch (Exception ex) { ShowToast($"형상 커널을 실행할 수 없습니다: {ex.Message}", 8); return; }
        if (seq != _regenSeq) return; // 더 새 요청이 있음
        if (r.Error != null) ShowToast(r.Error, 6);
        _last = r;
        ApplyResult(r, doc);
    }

    /// 스케치 편집 중에는 해당 스케치까지만 계산(타임라인 롤백)
    Document EffectiveDoc()
    {
        if (_mode == Mode.Sketch && _sketchId != null)
        {
            var d = _doc.Clone();
            int i = d.Features.FindIndex(f => f.Id == _sketchId);
            if (i >= 0) d.Marker = i + 1;
            return d;
        }
        return _doc;
    }

    void ApplyResult(RegenResult r, Document doc)
    {
        Scene.Bodies = r.Bodies;
        Scene.BodyVersion++;
        // 선택이 사라진 바디/면 정리
        var bodyIds = r.Bodies.Select(b => b.Index).ToHashSet();
        Scene.SelectedEdges.RemoveWhere(e => !bodyIds.Contains(e.Body));
        Scene.SelectedFaces.RemoveWhere(e => !bodyIds.Contains(e.Body));
        Scene.HiddenBodies.RemoveWhere(i => !bodyIds.Contains(i));

        var prevVisible = Scene.Sketches.ToDictionary(s => s.Sketch.Id, s => s.Visible);
        Scene.Sketches = doc.Features.Take(doc.ActiveCount).OfType<SketchFeature>().Where(s => !s.Suppressed).Select(s => new SketchView
        {
            Sketch = s,
            Profiles = r.Profiles.TryGetValue(s.Id, out var p) ? p : [],
            Active = _mode == Mode.Sketch && s.Id == _sketchId,
            Visible = _hiddenSketches.Contains(s.Id) ? false : prevVisible.GetValueOrDefault(s.Id, true),
        }).ToList();
        // 명령 중 프로파일 선택용으로 대상 스케치 강조
        if (_mode == Mode.Command && _cmdSketchId != null)
            foreach (var sv in Scene.Sketches) sv.Active = sv.Sketch.Id == _cmdSketchId;

        RebuildBrowser();
        RebuildTimeline();
        UpdateCommandError();
        Viewport.Invalidate();
    }

    readonly HashSet<string> _hiddenSketches = [];

    // ------------------------------------------------------------------ 파일

    async Task NewDocument()
    {
        if (!await ConfirmDiscard()) return;
        if (_mode != Mode.Idle) { CancelCommand(); ExitSketch(false); }
        _doc = new Document(); _path = null; _dirty = false; _undo.Clear(); _redo.Clear();
        _hiddenSketches.Clear(); Scene.ClearSelection();
        UpdateTitle();
        await Regenerate();
        Cam.Home(new Vector3(-50), new Vector3(50));
    }

    static readonly FilePickerFileType OfcdType = new("OpenFusionCAD 디자인") { Patterns = ["*.ofcd"] };
    static readonly FilePickerFileType CadType = new("CAD 파일 (STEP/IGES/BREP)") { Patterns = ["*.step", "*.stp", "*.iges", "*.igs", "*.brep"] };

    async Task OpenDialog()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "열기",
            FileTypeFilter = [OfcdType, CadType],
        });
        var p = files.FirstOrDefault()?.TryGetLocalPath();
        if (p != null) await OpenPath(p);
    }

    public async Task OpenPath(string p)
    {
        try
        {
            if (p.EndsWith(".ofcd", StringComparison.OrdinalIgnoreCase))
            {
                _doc = Document.FromJson(await File.ReadAllTextAsync(p));
                _path = p;
            }
            else
            {
                // STEP 등은 가져오기 피처 하나를 가진 새 디자인으로 연다
                _doc = new Document();
                _doc.Features.Add(new ImportFeature { Name = Path.GetFileName(p), Path = p });
                _path = null;
            }
            _dirty = false; _undo.Clear(); _redo.Clear(); _hiddenSketches.Clear(); Scene.ClearSelection();
            UpdateTitle();
            await Regenerate();
            var (a, b) = Scene.Bounds(); Cam.Home(a, b);
            AddRecent(p);
        }
        catch (Exception ex) { ShowToast($"열 수 없습니다: {ex.Message}", 6); }
    }

    async Task<bool> Save(bool saveAs = false)
    {
        if (_path == null || saveAs)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "다른 이름으로 저장",
                DefaultExtension = "ofcd",
                SuggestedFileName = _path != null ? Path.GetFileName(_path) : "Untitled.ofcd",
                FileTypeChoices = [OfcdType],
            });
            var p = f?.TryGetLocalPath();
            if (p == null) return false;
            _path = p;
        }
        await File.WriteAllTextAsync(_path, _doc.ToJson(indented: true));
        _dirty = false;
        UpdateTitle();
        AddRecent(_path);
        ShowToast($"저장됨: {_path}");
        return true;
    }

    async Task Export(string format)
    {
        if (Scene.Bodies.Count == 0) { ShowToast("내보낼 바디가 없습니다."); return; }
        var ext = format switch { "step" => "step", "stl" => "stl", "iges" => "igs", _ => "brep" };
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"{format.ToUpperInvariant()} 내보내기",
            DefaultExtension = ext,
            SuggestedFileName = (_path != null ? Path.GetFileNameWithoutExtension(_path) : "Untitled") + "." + ext,
        });
        var p = f?.TryGetLocalPath();
        if (p == null) return;
        var sel = Scene.SelectedBodies.Count > 0 ? Scene.SelectedBodies.ToList() : null;
        var err = await _kernel.ExportAsync(_doc, p, format, sel);
        ShowToast(err == null ? $"내보냄: {p}" : $"내보내기 실패: {err}", 6);
    }

    async Task InsertCad()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "삽입", FileTypeFilter = [CadType] });
        var p = files.FirstOrDefault()?.TryGetLocalPath();
        if (p != null) await InsertFile(p);
    }

    public async Task InsertFile(string p)
    {
        Snapshot();
        InsertFeature(new ImportFeature { Name = Path.GetFileName(p), Path = p });
        await Commit($"삽입됨: {Path.GetFileName(p)}");
        var (a, b) = Scene.Bounds(); Cam.Fit(a, b);
    }

    /// 타임라인 마커 위치에 피처 삽입
    void InsertFeature(Feature f)
    {
        int at = _doc.ActiveCount;
        _doc.Features.Insert(at, f);
        if (_doc.Marker != null) _doc.Marker = at + 1;
        if (_doc.Marker >= _doc.Features.Count) _doc.Marker = null;
    }

    bool _confirmedClose;
    async Task ConfirmClose()
    {
        if (await ConfirmDiscard()) { _confirmedClose = true; Close(); }
    }

    async Task<bool> ConfirmDiscard()
    {
        if (!_dirty) return true;
        var result = await Dialogs.Ask(this, "변경 내용 저장", "저장하지 않은 변경 내용이 있습니다. 저장하시겠습니까?", "저장", "저장 안 함", "취소");
        return result switch { 0 => await Save(), 1 => true, _ => false };
    }

    static string RecentFile => Path.Combine(Settings.ConfigDir, "recent.txt");
    static void AddRecent(string p)
    {
        try
        {
            var list = File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).ToList() : [];
            list.Remove(p); list.Insert(0, p);
            Directory.CreateDirectory(Settings.ConfigDir);
            File.WriteAllLines(RecentFile, list.Take(10));
        }
        catch { }
    }
    static List<string> Recent() => File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).Where(File.Exists).ToList() : [];

    // ------------------------------------------------------------------ 앱 바 / 툴바

    void BuildAppBar()
    {
        DataPanelButton.Content = Icons.Make("grid", 20);
        FileMenuButton.Content = Icons.Make("file", 20);
        SaveButton.Content = Icons.Make("save", 20);
        UndoButton.Content = Icons.Make("undo", 20);
        RedoButton.Content = Icons.Make("redo", 20);
        HelpButton.Content = Icons.Make("help", 20);
        FinishIcon.Content = Icons.Make("finish", 16);
        CloudIcon.Content = Icons.Make("cloud", 16);
        SaveButton.Click += async (_, _) => await Save();
        UndoButton.Click += async (_, _) => await Undo();
        RedoButton.Click += async (_, _) => await Redo();
        DataPanelButton.Click += (_, _) => ToggleDataPanel();
        HelpButton.Click += (_, _) => ShowToast("단축키: L 선 · R 사각형 · C 원 · A 호 · E 돌출 · F 필렛 · Del 삭제 · Esc 취소 · Ctrl+Z/Y · 휠 확대 · 가운데 버튼 이동 · Shift+가운데/오른쪽 버튼 회전", 10);

        FileMenuButton.Flyout = FileMenu;
        FileMenu.Opening += (_, _) =>
        {
            var items = new List<MenuItem>
            {
                Item("새 디자인", async () => await NewDocument(), "Ctrl+N"),
                Item("열기...", async () => await OpenDialog(), "Ctrl+O"),
                Item("저장", async () => await Save(), "Ctrl+S"),
                Item("다른 이름으로 저장...", async () => await Save(true)),
            };
            var recent = new MenuItem { Header = "최근 문서" };
            foreach (var r in Recent()) recent.Items.Add(Item(Path.GetFileName(r), async () => { if (await ConfirmDiscard()) await OpenPath(r); }));
            recent.IsEnabled = recent.Items.Count > 0;
            items.Add(recent);
            var exp = new MenuItem { Header = "내보내기" };
            exp.Items.Add(Item("STEP (*.step)", async () => await Export("step")));
            exp.Items.Add(Item("STL (*.stl)", async () => await Export("stl")));
            exp.Items.Add(Item("IGES (*.igs)", async () => await Export("iges")));
            exp.Items.Add(Item("OCCT BREP (*.brep)", async () => await Export("brep")));
            items.Add(exp);
            items.Add(Item("삽입 (STEP/IGES)...", async () => await InsertCad()));
            FileMenu.Items.Clear();
            foreach (var it in items) FileMenu.Items.Add(it);
        };
    }

    static MenuItem Item(string header, Func<Task> action, string? gesture = null)
    {
        var m = new MenuItem { Header = header, InputGesture = gesture != null ? KeyGesture.Parse(gesture) : null };
        m.Click += async (_, _) => await action();
        return m;
    }

    readonly MenuFlyout FileMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    readonly MenuFlyout UserMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight };

    string _tab = "solid";

    void BuildToolbar()
    {
        TabStrip.Children.Clear();
        var tabs = _mode == Mode.Sketch
            ? new[] { ("sketch", "스케치") }
            : new[] { ("solid", "솔리드"), ("surface", "곡면"), ("mesh", "메쉬"), ("sheet", "판금"), ("plastic", "플라스틱"), ("utilities", "유틸리티"), ("manage", "관리") };
        foreach (var (key, label) in tabs)
        {
            var b = new Button { Content = label, Classes = { "tab" } };
            if (key == (_mode == Mode.Sketch ? "sketch" : _tab)) b.Classes.Add(_mode == Mode.Sketch ? "sketch" : "active");
            b.Click += (_, _) =>
            {
                if (key is "solid" or "utilities") { _tab = key; BuildToolbar(); }
                else if (_mode != Mode.Sketch) ShowToast($"'{label}' 작업공간은 다음 단계에서 제공됩니다.");
            };
            TabStrip.Children.Add(b);
        }

        ToolGroups.Children.Clear();
        _sketchToolButtons.Clear();
        if (_mode == Mode.Sketch)
        {
            AddGroup("작성", [new("line", "선 (L)", () => SetSketchTool(SketchTool.Line), SketchTool.Line), new("rect", "2점 사각형 (R)", () => SetSketchTool(SketchTool.Rect), SketchTool.Rect),
                new("circle", "중심 지름 원 (C)", () => SetSketchTool(SketchTool.Circle), SketchTool.Circle), new("arc", "3점 호 (A)", () => SetSketchTool(SketchTool.Arc), SketchTool.Arc),
                new("construction", "작도선", () => SetSketchTool(SketchTool.Construction), SketchTool.Construction)]);
            AddGroup("수정", [new("trim", "선택 항목 삭제 (Del)", () => DeleteSelectedEntities())]);
            AddGroup("선택", [new("select", "선택 (Esc)", () => SetSketchTool(SketchTool.Select), SketchTool.Select)]);
            AddGroup("스케치 마치기", [new("finish", "스케치 마치기", () => ExitSketch(true))]);
            return;
        }
        if (_tab == "utilities")
        {
            AddGroup("내보내기", [new("export", "STEP 내보내기", () => _ = Export("step")), new("export", "STL 내보내기", () => _ = Export("stl"))]);
            AddGroup("삽입", [new("insert", "STEP/IGES 삽입", () => _ = InsertCad())]);
            return;
        }
        AddGroup("작성", [new("sketch", "스케치 작성", () => StartSketchPick()), new("extrude", "돌출 (E)", () => StartCommand("extrude")),
            new("revolve", "회전", () => StartCommand("revolve")), new("box", "상자", () => StartCommand("box")),
            new("cylinder", "원통", () => StartCommand("cylinder")), new("sphere", "구", () => StartCommand("sphere"))]);
        AddGroup("수정", [new("fillet", "모깎기 (F)", () => StartCommand("fillet")), new("chamfer", "모따기", () => StartCommand("chamfer"))]);
        AddGroup("구성", [new("plane", "원점 평면 표시", () => { Scene.ShowOrigin = !Scene.ShowOrigin; Viewport.Invalidate(); })]);
        AddGroup("검사", [new("measure", "측정", () => Measure())]);
        AddGroup("삽입", [new("insert", "STEP/IGES 삽입", () => _ = InsertCad())]);
        AddGroup("선택", [new("select", "선택", () => { CancelCommand(); Scene.ClearSelection(); Viewport.Invalidate(); })]);
    }

    readonly Dictionary<SketchTool, Button> _sketchToolButtons = [];

    record ToolDef(string Icon, string Tip, Action Act, SketchTool? Tool = null);

    void AddGroup(string title, ToolDef[] tools)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var t in tools)
        {
            var b = new Button { Classes = { "tool" }, Content = Icons.Make(t.Icon, 26) };
            ToolTip.SetTip(b, t.Tip);
            b.Click += (_, _) => { t.Act(); Viewport.Focus(); };
            icons.Children.Add(b);
            if (t.Tool is { } st) _sketchToolButtons[st] = b;
        }
        var label = new Button { Classes = { "group" }, Content = $"{title} ▾" };
        var flyout = new MenuFlyout();
        foreach (var t in tools) { var mi = new MenuItem { Header = t.Tip }; mi.Click += (_, _) => t.Act(); flyout.Items.Add(mi); }
        label.Flyout = flyout;
        var group = new StackPanel { Spacing = 0, Margin = new Thickness(6, 0) };
        group.Children.Add(icons);
        group.Children.Add(label);
        ToolGroups.Children.Add(group);
        ToolGroups.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.Parse("#E1E4E8")), Margin = new Thickness(0, 4) });
    }

    // ------------------------------------------------------------------ 탐색 막대

    enum NavMode { None, Orbit, Pan }
    NavMode _nav = NavMode.None;
    readonly Dictionary<NavMode, Button> _navButtons = [];

    void BuildNavBar()
    {
        void Add(string icon, string tip, Action act, NavMode? mode = null)
        {
            var b = new Button { Classes = { "nav" }, Content = Icons.Make(icon, 18) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { act(); Viewport.Focus(); };
            if (mode != null) _navButtons[mode.Value] = b;
            NavButtons.Children.Add(b);
        }
        Add("orbit", "궤도 (왼쪽 드래그로 회전)", () => SetNav(_nav == NavMode.Orbit ? NavMode.None : NavMode.Orbit), NavMode.Orbit);
        Add("pan", "초점 이동", () => SetNav(_nav == NavMode.Pan ? NavMode.None : NavMode.Pan), NavMode.Pan);
        Add("zoom", "확대", () => { Cam.Zoom(0.7f, Cam.Width / 2, Cam.Height / 2); Viewport.Invalidate(); });
        Add("fit", "맞춤", () => { var (a, b) = Scene.Bounds(); Cam.Fit(a, b); Viewport.Invalidate(); });
        Add("display", "투영 전환 (원근/직교)", () => { Cam.Ortho = !Cam.Ortho; ShowToast(Cam.Ortho ? "직교 투영" : "원근 투영", 2); Viewport.Invalidate(); });
        Add("grid", "그리드 표시", () => { Scene.ShowGrid = !Scene.ShowGrid; Viewport.Invalidate(); });
    }

    void SetNav(NavMode m)
    {
        _nav = m;
        foreach (var (k, b) in _navButtons) b.Classes.Set("checked", k == m);
    }

    void Measure()
    {
        if (Scene.Bodies.Count == 0) { ShowToast("측정할 바디가 없습니다."); return; }
        var lines = Scene.Bodies.Select(b =>
            $"{b.Name}: {b.BBox[3] - b.BBox[0]:0.##} × {b.BBox[4] - b.BBox[1]:0.##} × {b.BBox[5] - b.BBox[2]:0.##} mm (면 {b.FaceCount}, 모서리 {b.EdgeCount})");
        ShowToast(string.Join("\n", lines), 8);
    }

    // ------------------------------------------------------------------ 키보드

    async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && e.Key is not (Key.Escape)) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl: await Undo(); e.Handled = true; return;
            case Key.Y when ctrl: await Redo(); e.Handled = true; return;
            case Key.S when ctrl: await Save(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; return;
            case Key.O when ctrl: await OpenDialog(); e.Handled = true; return;
            case Key.N when ctrl: await NewDocument(); e.Handled = true; return;
        }
        if (ctrl) return;
        if (_mode == Mode.Sketch) { if (SketchKey(e)) e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape:
                if (_mode == Mode.Command) CancelCommand();
                else if (_mode == Mode.PickSketchPlane) EndSketchPick();
                else { Scene.ClearSelection(); Viewport.Invalidate(); }
                e.Handled = true; break;
            case Key.Enter when _mode == Mode.Command: await OkCommand(); e.Handled = true; break;
            case Key.E: StartCommand("extrude"); e.Handled = true; break;
            case Key.F: StartCommand("fillet"); e.Handled = true; break;
            case Key.Delete when _selectedFeature != null: await DeleteFeature(_selectedFeature); e.Handled = true; break;
        }
    }
}
