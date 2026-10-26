using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenFusionCAD.Model;

namespace OpenFusionCAD.UI;

public partial class MainWindow
{
    // ------------------------------------------------------------------ 타임라인

    void BuildTimelineControls()
    {
        void Add(string icon, string tip, Func<Task> act)
        {
            var b = new Button { Classes = { "tlc" }, Content = Icons.Make(icon, 14) };
            ToolTip.SetTip(b, tip);
            b.Click += async (_, _) => await act();
            TimelineControls.Children.Add(b);
        }
        Add("first", "처음으로 이동", () => SetMarker(0));
        Add("prev", "한 단계 뒤로 이동", () => SetMarker(Math.Max(0, _doc.ActiveCount - 1)));
        Add("play", "재생", () => SetMarker(Math.Min(_doc.Features.Count, _doc.ActiveCount + 1)));
        Add("last", "끝으로 이동", () => SetMarker(_doc.Features.Count));
        TimelineSettings.Flyout = new MenuFlyout
        {
            Items =
            {
                Mi("모든 피처 계산", () => _ = ComputeAll()),
                Mi("기록 마커를 끝으로", () => _ = SetMarker(_doc.Features.Count)),
            },
        };
    }

    static MenuItem Mi(string h, Action a) { var m = new MenuItem { Header = h }; m.Click += (_, _) => a(); return m; }

    async Task SetMarker(int m)
    {
        if (_mode == Mode.Command) CancelCommand(false);
        if (_mode == Mode.Sketch) ExitSketch(false);
        int? nm = m >= _doc.Features.Count ? null : Math.Max(0, m);
        if (nm == _doc.Marker) return;
        Snapshot();
        _doc.Marker = nm;
        await Commit();
    }

    Border? _markerCtl;

    void RebuildTimeline()
    {
        Timeline.Children.Clear();
        int active = _doc.ActiveCount;
        for (int i = 0; i < _doc.Features.Count; i++)
        {
            if (i == active) Timeline.Children.Add(_markerCtl = Marker());
            var f = _doc.Features[i];
            var st = _last?.StatusOf(f.Id);
            var b = new Button { Classes = { "tl" }, Content = Icons.Make(Icons.ForFeature(f.Kind), 20), Tag = i };
            if (f.Id == _selectedFeature) b.Classes.Add("selected");
            if (st is { Ok: false }) b.Classes.Add("failed");
            if (i >= active) b.Classes.Add("rolled");
            if (f.Suppressed) b.Classes.Add("suppressed");
            ToolTip.SetTip(b, f.Name + (f.Suppressed ? " (억제됨)" : "") + (st is { Ok: false } ? $"\n⚠ {st.Error}" : ""));
            b.Click += (_, _) => { _selectedFeature = f.Id; RebuildTimeline(); };
            b.DoubleTapped += (_, _) => EditFeature(f);
            b.ContextMenu = FeatureMenu(f, i);
            Timeline.Children.Add(b);
        }
        if (active >= _doc.Features.Count) Timeline.Children.Add(_markerCtl = Marker());
    }

    static Border Marker() => new()
    {
        Width = 6, Height = 32, Margin = new Thickness(1, 0),
        Background = new SolidColorBrush(Color.Parse("#2B2F36")), CornerRadius = new CornerRadius(2),
        Cursor = new Cursor(StandardCursorType.SizeWestEast),
        [ToolTip.TipProperty] = "기록 마커 – 끌어서 이동하면 이후 피처는 계산하지 않습니다",
    };

    /// 마커를 끌어 원하는 위치로 롤백 (Fusion 타임라인과 같음)
    void HookTimelineMarkerDrag()
    {
        bool dragging = false;
        Timeline.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v && (v == _markerCtl || v.GetVisualAncestors().Contains(_markerCtl)))
            {
                dragging = true;
                e.Pointer.Capture(Timeline);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Timeline.PointerReleased += async (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            var x = e.GetPosition(Timeline).X;
            // 마커 위치 = 커서보다 왼쪽에 중심이 있는 피처 수
            int count = Timeline.Children.OfType<Button>().Count(b => b.Bounds.X + b.Bounds.Width / 2 < x);
            await SetMarker(count);
        };
    }

    ContextMenu FeatureMenu(Feature f, int index)
    {
        var m = new ContextMenu();
        void Add(string header, Func<Task> act, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += async (_, _) => await act(); m.Items.Add(mi); }
        if (f is SketchFeature) Add("스케치 편집", () => { EditFeature(f); return Task.CompletedTask; });
        else Add("피처 편집", () => { EditFeature(f); return Task.CompletedTask; });
        var profileSketch = f switch { ExtrudeFeature e => e.Sketch, RevolveFeature r => r.Sketch, _ => null };
        if (profileSketch != null) Add("프로파일 스케치 편집", async () => { await EnterSketch(profileSketch); _sketchIsNew = false; });
        Add("기록 마커를 여기로 롤백", () => SetMarker(index + 1));
        Add(f.Suppressed ? "피처 억제 해제" : "피처 억제", async () => { Snapshot(); f.Suppressed = !f.Suppressed; await Commit(); });
        m.Items.Add(new Separator());
        Add("이름 바꾸기", () => RenameFeature(f));
        Add("삭제", () => DeleteFeature(f.Id));
        m.Items.Add(new Separator());
        Add("검색기에서 찾기", () => { _bodiesExpanded = true; _sketchesExpanded = true; RebuildBrowser(); return Task.CompletedTask; });
        return m;
    }

    void EditFeature(Feature f)
    {
        _selectedFeature = f.Id;
        switch (f)
        {
            case SketchFeature: _ = EnterSketch(f.Id); _sketchIsNew = false; break;
            case ExtrudeFeature: StartCommand("extrude", f); break;
            case RevolveFeature: StartCommand("revolve", f); break;
            case FilletFeature: StartCommand("fillet", f); break;
            case ChamferFeature: StartCommand("chamfer", f); break;
            case BoxFeature: StartCommand("box", f); break;
            case CylinderFeature: StartCommand("cylinder", f); break;
            case SphereFeature: StartCommand("sphere", f); break;
            case PressPullFeature: StartCommand("presspull", f); break;
            case HoleFeature: StartCommand("hole", f); break;
            case ShellFeature: StartCommand("shell", f); break;
            case MoveFeature: StartCommand("move", f); break;
            default: ShowToast("이 피처는 편집할 매개변수가 없습니다."); break;
        }
    }

    async Task RenameFeature(Feature f)
    {
        var name = await Dialogs.Prompt(this, "이름 바꾸기", "새 이름:", f.Name);
        if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
        Snapshot();
        f.Name = name.Trim();
        await Commit();
    }

    async Task DeleteFeature(string id)
    {
        var f = _doc.Find(id);
        if (f == null) return;
        if (_mode == Mode.Sketch && _sketchId == id) ExitSketch(false);
        if (_mode == Mode.Command) CancelCommand(false);
        var users = _doc.Features.Where(x => x is ExtrudeFeature e && e.Sketch == id || x is RevolveFeature r && r.Sketch == id).Select(x => x.Name).ToList();
        if (users.Count > 0)
        {
            var ok = await Dialogs.Ask(this, "피처 삭제", $"{f.Name} 을(를) 사용하는 피처가 있습니다: {string.Join(", ", users)}\n삭제하면 해당 피처가 실패합니다. 계속하시겠습니까?", "삭제", "취소");
            if (ok != 0) return;
        }
        Snapshot();
        int idx = _doc.Features.IndexOf(f);
        _doc.Features.Remove(f);
        if (_doc.Marker is { } mk && idx < mk) _doc.Marker = mk - 1;
        if (_selectedFeature == id) _selectedFeature = null;
        await Commit($"삭제됨: {f.Name}");
    }

    // ------------------------------------------------------------------ 검색기 (Fusion: 전구로 표시/숨기기)

    bool _originExpanded, _bodiesExpanded = true, _sketchesExpanded = true, _viewsExpanded;
    int _browserHighlightBody = -1;

    void RebuildBrowser()
    {
        BrowserTree.Children.Clear();
        BrowserScroll.IsVisible = BrowserCollapse.IsChecked == true;
        BrowserCollapse.Content = BrowserCollapse.IsChecked == true ? "▾" : "▸";
        BrowserCollapse.IsCheckedChanged -= OnBrowserCollapse;
        BrowserCollapse.IsCheckedChanged += OnBrowserCollapse;

        var docName = (_path != null ? System.IO.Path.GetFileNameWithoutExtension(_path) : "(저장되지 않음)") + " v1";
        Row(0, "component", docName, expand: true, bold: true);
        Row(1, "settings", "문서 설정", tip: "단위: mm");
        Row(1, "folder", "명명된 뷰", expand: _viewsExpanded, onExpand: () => { _viewsExpanded = !_viewsExpanded; RebuildBrowser(); });
        if (_viewsExpanded)
            foreach (var (name, dir) in new[] { ("위", new System.Numerics.Vector3(0, 0, 1)), ("앞", new System.Numerics.Vector3(0, -1, 0)), ("오른쪽", new System.Numerics.Vector3(1, 0, 0)), ("홈", new System.Numerics.Vector3(1, -1, 0.8f)) })
                Row(2, "lookat", name, click: () => { Cam.LookFrom(dir); Viewport.Invalidate(); });
        Row(1, "folder", "원점", visible: Scene.ShowOrigin, expand: _originExpanded,
            toggle: () => { Scene.ShowOrigin = !Scene.ShowOrigin; RebuildBrowser(); Viewport.Invalidate(); },
            onExpand: () => { _originExpanded = !_originExpanded; RebuildBrowser(); });
        if (_originExpanded)
            foreach (var n in new[] { "O", "X", "Y", "Z", "XY", "XZ", "YZ" })
                Row(2, n.Length == 2 ? "plane" : n == "O" ? "point" : "axis", n, visible: Scene.ShowOrigin, toggle: () => { Scene.ShowOrigin = !Scene.ShowOrigin; RebuildBrowser(); Viewport.Invalidate(); });

        var bodies = Scene.Bodies;
        Row(1, "folder", "바디", visible: bodies.Count == 0 || bodies.Any(b => !Scene.HiddenBodies.Contains(b.Index)), expand: _bodiesExpanded,
            toggle: () =>
            {
                bool anyVisible = bodies.Any(b => !Scene.HiddenBodies.Contains(b.Index));
                _doc.HiddenBodies = anyVisible ? bodies.Select(b => b.Index).ToList() : [];
                Scene.HiddenBodies = _doc.HiddenBodies.ToHashSet();
                RebuildBrowser(); Viewport.Invalidate();
            },
            onExpand: () => { _bodiesExpanded = !_bodiesExpanded; RebuildBrowser(); });
        if (_bodiesExpanded)
            foreach (var b in bodies)
            {
                bool vis = !Scene.HiddenBodies.Contains(b.Index);
                var feat = _doc.Find(b.FeatureId);
                Row(2, "body", b.Name, visible: vis,
                    toggle: () => { if (!_doc.HiddenBodies.Remove(b.Index)) _doc.HiddenBodies.Add(b.Index); Scene.HiddenBodies = _doc.HiddenBodies.ToHashSet(); RebuildBrowser(); Viewport.Invalidate(); },
                    click: () => { Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(b.Index); _browserHighlightBody = b.Index; RebuildBrowser(); Viewport.Invalidate(); },
                    tip: feat != null ? $"생성 피처: {feat.Name}" : null,
                    highlight: b.Index == _browserHighlightBody || Scene.SelectedBodies.Contains(b.Index),
                    menu: BodyMenu(b.Index));
            }
        var sketches = Scene.Sketches;
        Row(1, "folder", "스케치", visible: sketches.Count == 0 || sketches.Any(s => s.Visible), expand: _sketchesExpanded,
            toggle: () =>
            {
                bool any = sketches.Any(s => s.Visible);
                foreach (var sv in sketches) { sv.Visible = !any; if (any) HideSketch(sv.Sketch.Id); else _doc.HiddenSketches.Remove(sv.Sketch.Id); }
                RebuildBrowser(); Viewport.Invalidate();
            },
            onExpand: () => { _sketchesExpanded = !_sketchesExpanded; RebuildBrowser(); });
        if (_sketchesExpanded)
            foreach (var sv in sketches)
                Row(2, "sketch", sv.Sketch.Name, visible: sv.Visible || sv.Active,
                    toggle: () =>
                    {
                        sv.Visible = !sv.Visible;
                        if (sv.Visible) _doc.HiddenSketches.Remove(sv.Sketch.Id); else HideSketch(sv.Sketch.Id);
                        RebuildBrowser(); Viewport.Invalidate();
                    },
                    dbl: () => { _ = EnterSketch(sv.Sketch.Id); _sketchIsNew = false; },
                    bold: sv.Active,
                    menu: new ContextMenu { Items = { Mi("스케치 편집", () => { _ = EnterSketch(sv.Sketch.Id); _sketchIsNew = false; }), Mi("다음 보기", () => { var f = new Sketching.Frame(sv.Sketch.Plane); Cam.LookFrom(f.N); Viewport.Invalidate(); }), Mi("이름 바꾸기", () => _ = RenameFeature(sv.Sketch)), Mi("삭제", () => _ = DeleteFeature(sv.Sketch.Id)) } });
    }

    ContextMenu BodyMenu(int body) => new()
    {
        Items =
        {
            Mi("표시/숨기기", () => { if (!_doc.HiddenBodies.Remove(body)) _doc.HiddenBodies.Add(body); Scene.HiddenBodies = _doc.HiddenBodies.ToHashSet(); RebuildBrowser(); Viewport.Invalidate(); }),
            Mi("모양...", () => { Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(body); _ = ShowAppearance(); }),
            Mi("이동/복사", () => { Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(body); StartCommand("move"); }),
            Mi("STL 로 저장", () => { Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(body); _ = Export("stl"); }),
            Mi("삭제", () => { Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(body); _ = DeleteSelection(); }),
        },
    };

    void OnBrowserCollapse(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BrowserScroll.IsVisible = BrowserCollapse.IsChecked == true;
        BrowserCollapse.Content = BrowserCollapse.IsChecked == true ? "▾" : "▸";
    }

    void Row(int depth, string icon, string text, bool? visible = null, Action? toggle = null, Action? click = null, Action? dbl = null,
             bool bold = false, string? tip = null, bool? expand = null, Action? onExpand = null, bool highlight = false, ContextMenu? menu = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(depth * 14, 0, 0, 0) };
        if (expand != null)
        {
            var ex = new Button { Classes = { "eye" }, Content = new TextBlock { Text = expand.Value ? "▾" : "▸", FontSize = 10 }, Width = 14 };
            ex.Click += (_, _) => onExpand?.Invoke();
            sp.Children.Add(ex);
        }
        else sp.Children.Add(new Border { Width = 14 });
        if (visible != null)
        {
            var bulb = new Button { Classes = { "eye" }, Content = Icons.Make(visible.Value ? "bulb" : "bulboff", 15) };
            ToolTip.SetTip(bulb, visible.Value ? "숨기기" : "표시");
            bulb.Click += (_, _) => toggle?.Invoke();
            sp.Children.Add(bulb);
        }
        else sp.Children.Add(new Border { Width = 22 });
        sp.Children.Add(Icons.Make(icon, 15));
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = new SolidColorBrush(Color.Parse(visible == false ? "#9AA0A8" : "#2B2F36")) });
        var row = new Border { Classes = { "treerow" }, Child = sp };
        if (highlight) row.Background = new SolidColorBrush(Color.Parse("#CDE8F7"));
        if (tip != null) ToolTip.SetTip(row, tip);
        if (click != null) row.PointerPressed += (_, e) => { if (e.ClickCount == 1 && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) click(); };
        if (dbl != null) row.DoubleTapped += (_, _) => dbl();
        if (menu != null) row.ContextMenu = menu;
        BrowserTree.Children.Add(row);
    }
}
