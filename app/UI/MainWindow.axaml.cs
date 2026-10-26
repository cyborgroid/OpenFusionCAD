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
        _marking = MarkingLayer;
        BuildCommands();
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
        HookShortcutBox();
        HookTimelineMarkerDrag();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
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
        Scene.HiddenBodies = _doc.HiddenBodies.ToHashSet();
        Scene.BodyColors = Scene.Bodies.ToDictionary(b => b.Index, b => Appearances.Color(_doc.Appearances.GetValueOrDefault(b.Index.ToString())));

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

    /// 숨긴 스케치 (문서에 저장)
    List<string> _hiddenSketches => _doc.HiddenSketches;

    void HideSketch(string id) { if (!_doc.HiddenSketches.Contains(id)) _doc.HiddenSketches.Add(id); }

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

    // ------------------------------------------------------------------ 응용프로그램 막대

    void BuildAppBar()
    {
        DataPanelButton.Content = Icons.Make("grid", 20);
        FileMenuButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { Icons.Make("file", 18), new TextBlock { Text = "▾", FontSize = 9, VerticalAlignment = VerticalAlignment.Center } } };
        SaveButton.Content = Icons.Make("save", 20);
        UndoButton.Content = Icons.Make("undo", 20);
        RedoButton.Content = Icons.Make("redo", 20);
        ExtensionsButton.Content = Icons.Make("extensions", 19);
        JobStatusButton.Content = Icons.Make("jobstatus", 19);
        NotifyButton.Content = Icons.Make("bell", 19);
        HelpButton.Content = Icons.Make("help", 19);
        DocTabIcon.Content = Icons.Make("body", 14);
        FinishIcon.Content = Icons.Make("finish", 16);
        CommandHelp.Content = Icons.Make("info", 18);
        TimelineSettings.Content = Icons.Make("settings", 14);
        SaveButton.Click += async (_, _) => await Save();
        UndoButton.Click += async (_, _) => await Undo();
        RedoButton.Click += async (_, _) => await Redo();
        DataPanelButton.Click += (_, _) => ToggleDataPanel();
        NewTabButton.Click += async (_, _) => await NewDocument();
        DocCloseButton.Click += async (_, _) => await NewDocument();
        ExtensionsButton.Click += (_, _) => Run("scripts");
        JobStatusButton.Click += (_, _) => ShowToast(_last?.Features.Any(f => !f.Ok) == true ? "계산 오류가 있는 피처가 있습니다. 타임라인의 빨간 아이콘을 확인하십시오." : "진행 중인 작업이 없습니다.");
        NotifyButton.Click += (_, _) => ShowToast("새 알림이 없습니다.");
        HelpButton.Click += (_, _) => ShowToast("S: 바로 가기 상자 · Q 누르기/당기기 · E 돌출 · H 구멍 · F 모깎기 · M 이동/복사 · I 측정 · A 모양 · V 가시성 · 스케치: L 선 · R 직사각형 · C 원 · D 치수 · X 구성 · 가운데 버튼 이동 · Shift+가운데 궤도 · 오른쪽 클릭 마킹 메뉴", 12);
        CommentsToggle.Click += (_, _) => ShowToast("주석은 클라우드 공유 문서에서 사용할 수 있으며 다음 단계에서 구현됩니다.");
        CommentsAdd.Click += (_, _) => ShowToast("주석은 다음 단계에서 구현됩니다.");
        WorkspaceButton.Flyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = "디자인", Icon = Icons.Make("finish", 14) },
                new MenuItem { Header = "생성형 디자인", IsEnabled = false }, new MenuItem { Header = "렌더링", IsEnabled = false },
                new MenuItem { Header = "애니메이션", IsEnabled = false }, new MenuItem { Header = "시뮬레이션", IsEnabled = false },
                new MenuItem { Header = "제조", IsEnabled = false }, new MenuItem { Header = "도면", IsEnabled = false },
            },
        };

        FileMenuButton.Flyout = FileMenu;
        FileMenu.Opening += (_, _) =>
        {
            FileMenu.Items.Clear();
            void Add(string header, Func<Task>? act, string? gesture = null)
            {
                var m = new MenuItem { Header = header, InputGesture = gesture != null ? KeyGesture.Parse(gesture) : null, IsEnabled = act != null };
                if (act != null) m.Click += async (_, _) => await act();
                FileMenu.Items.Add(m);
            }
            Add("새 디자인", NewDocument, "Ctrl+N");
            Add("새 도면", null);
            Add("열기...", OpenDialog, "Ctrl+O");
            var recent = new MenuItem { Header = "최근 문서 열기" };
            foreach (var r in Recent()) { var rr = r; var mi = new MenuItem { Header = Path.GetFileName(r) }; mi.Click += async (_, _) => { if (await ConfirmDiscard()) await OpenPath(rr); }; recent.Items.Add(mi); }
            recent.IsEnabled = recent.Items.Count > 0;
            FileMenu.Items.Add(recent);
            FileMenu.Items.Add(new Separator());
            Add("업로드", null);
            Add("저장", async () => await Save(), "Ctrl+S");
            Add("다른 이름으로 저장", async () => await Save(true), "Ctrl+Shift+S");
            Add("내보내기...", ExportDialog);
            Add("3D 인쇄", async () => await Export("stl"));
            Add("이미지 캡처", null);
            FileMenu.Items.Add(new Separator());
            Add("CAD 파일 삽입 (STEP/IGES)...", InsertCad);
        };
    }

    readonly MenuFlyout FileMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    readonly MenuFlyout UserMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight };

    /// Fusion 의 내보내기 대화상자: 유형 선택 후 저장 위치
    async Task ExportDialog()
    {
        if (Scene.Bodies.Count == 0) { ShowToast("내보낼 바디가 없습니다."); return; }
        var choice = await Dialogs.Ask(this, "내보내기", "유형을 선택하십시오. (선택한 바디가 있으면 그 바디만 내보냅니다)", "STEP 파일 (*.step)", "STL 파일 (*.stl)", "IGES 파일 (*.igs)", "OCCT BREP (*.brep)", "취소");
        string? fmt = choice switch { 0 => "step", 1 => "stl", 2 => "iges", 3 => "brep", _ => null };
        if (fmt != null) await Export(fmt);
    }

    async Task ComputeAll()
    {
        await Regenerate();
        ShowToast("모두 계산했습니다.", 2);
    }

    // ------------------------------------------------------------------ 탐색 막대 (Fusion 순서)

    enum NavMode { None, Orbit, Pan, Zoom }
    NavMode _nav = NavMode.None;
    readonly Dictionary<NavMode, Button> _navButtons = [];
    public enum VisualStyle { ShadedEdges, Shaded, Wireframe }

    void BuildNavBar()
    {
        Button Add(string icon, string tip, Action? act, NavMode? mode = null, MenuFlyout? flyout = null)
        {
            var content = flyout == null ? Icons.Make(icon, 18) : (Control)new StackPanel { Orientation = Orientation.Horizontal, Children = { Icons.Make(icon, 18), new TextBlock { Text = "▾", FontSize = 8, VerticalAlignment = VerticalAlignment.Bottom } } };
            var b = new Button { Classes = { "nav" }, Content = content, Width = flyout == null ? 30 : 38 };
            ToolTip.SetTip(b, tip);
            if (act != null) b.Click += (_, _) => { act(); Viewport.Focus(); };
            if (flyout != null) b.Flyout = flyout;
            if (mode != null) _navButtons[mode.Value] = b;
            NavButtons.Children.Add(b);
            return b;
        }
        void Sep() => NavButtons.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.Parse("#D3D6DB")), Margin = new Thickness(3, 4) });

        Add("orbit", "궤도 (Shift + 가운데 버튼)", () => SetNav(_nav == NavMode.Orbit ? NavMode.None : NavMode.Orbit), NavMode.Orbit);
        Add("lookat", "다음 보기", LookAtSelection);
        Sep();
        Add("pan", "초점이동 (가운데 버튼)", () => SetNav(_nav == NavMode.Pan ? NavMode.None : NavMode.Pan), NavMode.Pan);
        Add("zoom", "줌 (휠)", () => SetNav(_nav == NavMode.Zoom ? NavMode.None : NavMode.Zoom), NavMode.Zoom);
        Add("fit", "맞춤 (F6)", FitView);
        Sep();
        var display = new MenuFlyout();
        var style = new MenuItem { Header = "비주얼 스타일" };
        foreach (var (vs, label) in new[] { (VisualStyle.ShadedEdges, "모서리가 있는 음영처리"), (VisualStyle.Shaded, "음영처리"), (VisualStyle.Wireframe, "와이어프레임") })
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => { Scene.Style = vs; Viewport.Invalidate(); };
            style.Items.Add(mi);
        }
        display.Items.Add(style);
        var cam = new MenuItem { Header = "카메라" };
        var ortho = new MenuItem { Header = "직교" }; ortho.Click += (_, _) => { Cam.Ortho = true; Viewport.Invalidate(); };
        var persp = new MenuItem { Header = "원근" }; persp.Click += (_, _) => { Cam.Ortho = false; Viewport.Invalidate(); };
        cam.Items.Add(ortho); cam.Items.Add(persp);
        display.Items.Add(cam);
        var vis = new MenuItem { Header = "객체 가시성" };
        var o1 = new MenuItem { Header = "원점" }; o1.Click += (_, _) => { Scene.ShowOrigin = !Scene.ShowOrigin; RebuildBrowser(); Viewport.Invalidate(); };
        var o2 = new MenuItem { Header = "스케치" }; o2.Click += (_, _) => { foreach (var s in Scene.Sketches) s.Visible = !s.Visible; Viewport.Invalidate(); };
        vis.Items.Add(o1); vis.Items.Add(o2);
        display.Items.Add(vis);
        display.Items.Add(new MenuItem { Header = "환경", IsEnabled = false });
        display.Items.Add(new MenuItem { Header = "효과", IsEnabled = false });
        Add("display", "화면표시 설정", null, flyout: display);
        var grid = new MenuFlyout();
        var lg = new MenuItem { Header = "레이아웃 그리드" }; lg.Click += (_, _) => { Scene.ShowGrid = !Scene.ShowGrid; Viewport.Invalidate(); };
        var sg = new MenuItem { Header = "그리드에 스냅" }; sg.Click += (_, _) => { SnapGrid.IsChecked = SnapGrid.IsChecked != true; ShowToast(SnapGrid.IsChecked == true ? "그리드 스냅 켜짐" : "그리드 스냅 꺼짐", 2); };
        grid.Items.Add(lg); grid.Items.Add(sg);
        grid.Items.Add(new MenuItem { Header = "증분 이동", IsEnabled = false });
        Add("gridsnap", "그리드 및 스냅", null, flyout: grid);
        var vpf = new MenuFlyout { Items = { new MenuItem { Header = "단일 뷰" }, new MenuItem { Header = "다중 뷰", IsEnabled = false } } };
        Add("viewports", "뷰포트", null, flyout: vpf);
    }

    void SetNav(NavMode m)
    {
        _nav = m;
        foreach (var (k, b) in _navButtons) b.Classes.Set("checked", k == m);
    }

    void FitView()
    {
        var (a, b) = Scene.Bounds();
        Cam.Fit(a, b);
        Viewport.Invalidate();
    }

    /// 다음 보기: 선택한 평평한 면(또는 활성 스케치 평면)을 정면에서 본다
    void LookAtSelection()
    {
        if (_mode == Mode.Sketch && ActiveSketch is { } sk)
        {
            var f = new Sketching.Frame(sk.Plane);
            Cam.LookFrom(f.N, f.To3(f.To2(Cam.Target).X, f.To2(Cam.Target).Y));
        }
        else if (Scene.SelectedFaces.FirstOrDefault() is var fr && Scene.SelectedFaces.Count > 0 && IsPlanar(fr))
        {
            var pl = PlaneOfFace(fr);
            Cam.LookFrom(new Vector3((float)pl.Normal[0], (float)pl.Normal[1], (float)pl.Normal[2]), new Vector3((float)pl.Origin[0], (float)pl.Origin[1], (float)pl.Origin[2]));
        }
        else { ShowToast("다음 보기: 평평한 면을 선택하십시오."); return; }
        Viewport.Invalidate();
    }

    /// V: 선택한 바디의 표시/숨기기 전환
    void ToggleVisibility()
    {
        if (Scene.SelectedBodies.Count == 0 && Scene.SelectedFaces.Count == 0) { ShowToast("표시/숨길 바디를 선택하십시오."); return; }
        var bodies = Scene.SelectedBodies.Concat(Scene.SelectedFaces.Select(f => f.Body)).Distinct().ToList();
        foreach (var b in bodies)
            if (!_doc.HiddenBodies.Remove(b)) _doc.HiddenBodies.Add(b);
        Scene.HiddenBodies = _doc.HiddenBodies.ToHashSet();
        Scene.ClearSelection();
        _dirty = true; UpdateTitle();
        RebuildBrowser();
        Viewport.Invalidate();
    }

    void FindInBrowser()
    {
        var b = Scene.SelectedBodies.Concat(Scene.SelectedFaces.Select(f => f.Body)).FirstOrDefault(-1);
        if (b < 0) { ShowToast("찾을 바디를 선택하십시오."); return; }
        _browserHighlightBody = b;
        _bodiesExpanded = true;
        RebuildBrowser();
    }

    /// Delete: 캔버스에서 선택한 바디를 삭제(제거 피처), 아니면 선택한 타임라인 피처 삭제
    async Task DeleteSelection()
    {
        var bodies = Scene.SelectedBodies.Concat(Scene.SelectedFaces.Select(f => f.Body)).Concat(Scene.SelectedEdges.Select(e => e.Body)).Distinct().ToList();
        if (bodies.Count > 0)
        {
            Snapshot();
            foreach (var b in bodies) InsertFeature(new RemoveFeature { Name = _doc.NextName("Remove"), Body = b });
            Scene.ClearSelection();
            await Commit($"바디 {bodies.Count}개를 삭제했습니다.");
            return;
        }
        if (_selectedFeature != null) await DeleteFeature(_selectedFeature);
    }
}
