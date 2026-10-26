using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
        Add("first", "처음으로", () => SetMarker(0));
        Add("prev", "한 단계 뒤로", () => SetMarker(Math.Max(0, _doc.ActiveCount - 1)));
        Add("play", "한 단계 앞으로", () => SetMarker(Math.Min(_doc.Features.Count, _doc.ActiveCount + 1)));
        Add("last", "끝으로", () => SetMarker(_doc.Features.Count));
    }

    async Task SetMarker(int m)
    {
        if (_mode == Mode.Command) CancelCommand(false);
        if (_mode == Mode.Sketch) ExitSketch(false);
        int? nm = m >= _doc.Features.Count ? null : m;
        if (nm == _doc.Marker) return;
        Snapshot();
        _doc.Marker = nm;
        await Commit();
    }

    void RebuildTimeline()
    {
        Timeline.Children.Clear();
        int active = _doc.ActiveCount;
        for (int i = 0; i < _doc.Features.Count; i++)
        {
            if (i == active) Timeline.Children.Add(Marker());
            var f = _doc.Features[i];
            var st = _last?.StatusOf(f.Id);
            var b = new Button { Classes = { "tl" }, Content = Icons.Make(Icons.ForFeature(f.Kind), 20) };
            if (f.Id == _selectedFeature) b.Classes.Add("selected");
            if (st is { Ok: false }) b.Classes.Add("failed");
            if (i >= active) b.Classes.Add("rolled");
            if (f.Suppressed) b.Classes.Add("suppressed");
            ToolTip.SetTip(b, f.Name + (f.Suppressed ? " (억제됨)" : "") + (st is { Ok: false } ? $"\n⚠ {st.Error}" : ""));
            b.Click += (_, _) => { _selectedFeature = f.Id; RebuildTimeline(); RebuildBrowser(); };
            b.DoubleTapped += (_, _) => EditFeature(f);
            b.ContextMenu = FeatureMenu(f, i);
            Timeline.Children.Add(b);
        }
        if (active >= _doc.Features.Count) Timeline.Children.Add(Marker());
    }

    static Control Marker() => new Border
    {
        Width = 4, Height = 30, Margin = new Thickness(2, 0),
        Background = new SolidColorBrush(Color.Parse("#2B2F36")), CornerRadius = new CornerRadius(1),
        [ToolTip.TipProperty] = "타임라인 마커 (이후 피처는 계산하지 않음)",
    };

    ContextMenu FeatureMenu(Feature f, int index)
    {
        var m = new ContextMenu();
        void Add(string header, Func<Task> act) { var mi = new MenuItem { Header = header }; mi.Click += async (_, _) => await act(); m.Items.Add(mi); }
        Add(f is SketchFeature ? "스케치 편집" : "피처 편집", () => { EditFeature(f); return Task.CompletedTask; });
        Add("이름 바꾸기", () => RenameFeature(f));
        Add(f.Suppressed ? "억제 해제" : "피처 억제", async () => { Snapshot(); f.Suppressed = !f.Suppressed; await Commit(); });
        Add("여기까지 롤백", () => SetMarker(index + 1));
        Add("삭제", () => DeleteFeature(f.Id));
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

    // ------------------------------------------------------------------ 검색기

    void RebuildBrowser()
    {
        BrowserTree.Children.Clear();
        BrowserScroll.IsVisible = BrowserCollapse.IsChecked == true;
        BrowserCollapse.IsCheckedChanged -= OnBrowserCollapse;
        BrowserCollapse.IsCheckedChanged += OnBrowserCollapse;

        var docName = _path != null ? System.IO.Path.GetFileNameWithoutExtension(_path) : "(저장되지 않음)";
        Row(0, "file", docName, bold: true);
        Row(1, "settings", "문서 설정  ·  단위: mm");
        Row(1, "origin", "원점", visible: Scene.ShowOrigin, toggle: () => { Scene.ShowOrigin = !Scene.ShowOrigin; RebuildBrowser(); Viewport.Invalidate(); });

        Row(1, "folder", $"바디 ({Scene.Bodies.Count})");
        foreach (var b in Scene.Bodies)
        {
            bool vis = !Scene.HiddenBodies.Contains(b.Index);
            var feat = _doc.Find(b.FeatureId);
            Row(2, "body", b.Name, visible: vis,
                toggle: () => { if (!Scene.HiddenBodies.Remove(b.Index)) Scene.HiddenBodies.Add(b.Index); RebuildBrowser(); Viewport.Invalidate(); },
                click: () => { Scene.SelectedBodies.Clear(); Scene.SelectedBodies.Add(b.Index); Viewport.Invalidate(); },
                tip: feat != null ? $"생성 피처: {feat.Name}" : null);
        }
        var sketches = Scene.Sketches;
        Row(1, "folder", $"스케치 ({sketches.Count})");
        foreach (var sv in sketches)
        {
            Row(2, "sketch", sv.Sketch.Name, visible: sv.Visible || sv.Active,
                toggle: () =>
                {
                    sv.Visible = !sv.Visible;
                    if (sv.Visible) _hiddenSketches.Remove(sv.Sketch.Id); else _hiddenSketches.Add(sv.Sketch.Id);
                    RebuildBrowser(); Viewport.Invalidate();
                },
                dbl: () => { _ = EnterSketch(sv.Sketch.Id); _sketchIsNew = false; },
                bold: sv.Active);
        }
    }

    void OnBrowserCollapse(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BrowserScroll.IsVisible = BrowserCollapse.IsChecked == true;
        BrowserCollapse.Content = BrowserCollapse.IsChecked == true ? "–" : "+";
    }

    void Row(int depth, string icon, string text, bool? visible = null, Action? toggle = null, Action? click = null, Action? dbl = null, bool bold = false, string? tip = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(depth * 14, 0, 0, 0) };
        if (visible != null)
        {
            var eye = new Button { Classes = { "eye" }, Content = Icons.Make(visible.Value ? "eye" : "eyeoff", 14) };
            ToolTip.SetTip(eye, visible.Value ? "숨기기" : "표시");
            eye.Click += (_, _) => toggle?.Invoke();
            sp.Children.Add(eye);
        }
        else sp.Children.Add(new Border { Width = 22 });
        sp.Children.Add(Icons.Make(icon, 15));
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = new SolidColorBrush(Color.Parse("#2B2F36")) });
        var row = new Border { Classes = { "treerow" }, Child = sp };
        if (tip != null) ToolTip.SetTip(row, tip);
        if (click != null) row.PointerPressed += (_, e) => { if (e.ClickCount == 1) click(); };
        if (dbl != null) row.DoubleTapped += (_, _) => dbl();
        BrowserTree.Children.Add(row);
    }
}
