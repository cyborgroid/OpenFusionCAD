using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Sketching;
using OpenFusionCAD.Viewport;

namespace OpenFusionCAD.UI;

public enum SketchTool { Select, Line, Rect, CenterRect, Circle, Arc, PolygonCircumscribed, PolygonInscribed, Point, Dimension, Construction }

public partial class MainWindow
{
    string? _sketchId;
    bool _sketchIsNew;
    SketchTool _tool = SketchTool.Line;
    bool _constructionMode;
    int _polygonSides = 6;
    readonly List<Vector2> _clicks = [];
    Vector2 _cursor2;
    Vector2 _cursorPx;

    SketchFeature? ActiveSketch => _sketchId != null ? _doc.Find(_sketchId) as SketchFeature : null;

    // ------------------------------------------------------------------ 마우스 입력

    Point _downPos, _lastPos;
    MouseButton _downButton = MouseButton.None;
    bool _dragging;

    bool _boxSelecting, _manipDragging;
    float _manipStartValue;

    void HookViewportInput()
    {
        Viewport.PointerPressed += (_, e) =>
        {
            Viewport.Focus();
            HideMarkingMenu();
            var pt = e.GetCurrentPoint(Viewport);
            _downPos = _lastPos = pt.Position;
            _dragging = false; _boxSelecting = false; _manipDragging = false;
            _downButton = pt.Properties.IsLeftButtonPressed ? MouseButton.Left : pt.Properties.IsMiddleButtonPressed ? MouseButton.Middle : pt.Properties.IsRightButtonPressed ? MouseButton.Right : MouseButton.None;
            // 돌출 조작기 화살표를 잡으면 거리 끌기
            if (_downButton == MouseButton.Left && _mode == Mode.Command && Scene.Manipulator is { } mp && ManipulatorHit((float)pt.Position.X, (float)pt.Position.Y))
            {
                _manipDragging = true;
                _manipStartValue = mp.length;
            }
            e.Pointer.Capture(Viewport);
        };
        Viewport.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(Viewport);
            var d = p - _lastPos;
            if (_downButton != MouseButton.None && Distance(p, _downPos) > 4) _dragging = true;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (_dragging)
            {
                // Fusion 기본: 가운데 = 초점이동, Shift+가운데 = 궤도, 휠 = 줌. 탐색 막대 모드에서는 왼쪽 버튼으로도.
                bool orbit = (_downButton == MouseButton.Middle && shift) || (_downButton == MouseButton.Left && _nav == NavMode.Orbit);
                bool pan = (_downButton == MouseButton.Middle && !shift) || (_downButton == MouseButton.Left && _nav == NavMode.Pan);
                bool zoom = _downButton == MouseButton.Left && _nav == NavMode.Zoom;
                if (_manipDragging) DragManipulator((float)p.X, (float)p.Y);
                else if (orbit) Cam.Orbit((float)d.X, (float)d.Y);
                else if (pan) Cam.Pan((float)-d.X, (float)d.Y);
                else if (zoom) Cam.Zoom(MathF.Pow(1.01f, (float)d.Y), (float)_downPos.X, (float)_downPos.Y);
                else if (_downButton == MouseButton.Left && _nav == NavMode.None && _mode is Mode.Idle or Mode.Sketch) UpdateBoxSelection(p);
                Viewport.Invalidate();
            }
            _lastPos = p;
            if (!_dragging) Hover((float)p.X, (float)p.Y);
        };
        Viewport.PointerReleased += async (_, e) =>
        {
            var p = e.GetPosition(Viewport);
            var button = _downButton;
            bool wasDrag = _dragging;
            _downButton = MouseButton.None;
            _dragging = false;
            e.Pointer.Capture(null);
            if (_manipDragging) { _manipDragging = false; return; }
            if (_boxSelecting) { FinishBoxSelection(p, e.KeyModifiers); return; }
            if (wasDrag) return;
            if (button == MouseButton.Left && _nav == NavMode.None) await Click((float)p.X, (float)p.Y, e.KeyModifiers);
            else if (button == MouseButton.Right)
            {
                // Fusion: 오른쪽 클릭 = 마킹 메뉴 (스케치에서 그리는 중이면 체인 종료 후 메뉴)
                if (_mode == Mode.Sketch && _clicks.Count > 0) { EndChain(); return; }
                ShowMarkingMenu(e.GetPosition(CanvasArea));
            }
        };
        Viewport.PointerWheelChanged += (_, e) =>
        {
            var p = e.GetPosition(Viewport);
            Cam.Zoom(MathF.Pow(0.85f, (float)e.Delta.Y), (float)p.X, (float)p.Y);
            Viewport.Invalidate();
        };
        Viewport.DoubleTapped += (_, _) => { if (_mode == Mode.Sketch && _tool == SketchTool.Line) EndChain(); };
        Viewport.PointerExited += (_, _) => { Scene.HoverFace = null; Scene.HoverEdge = null; Viewport.Invalidate(); };
    }

    // ------------------------------------------------------------------ 창/교차 선택 (Fusion: 왼→오 = 창, 오→왼 = 교차)

    void UpdateBoxSelection(Point p)
    {
        _boxSelecting = true;
        bool crossing = p.X < _downPos.X;
        double x = Math.Min(p.X, _downPos.X), y = Math.Min(p.Y, _downPos.Y);
        SelectionRect.Margin = new Thickness(x, y, 0, 0);
        SelectionRect.Width = Math.Abs(p.X - _downPos.X);
        SelectionRect.Height = Math.Abs(p.Y - _downPos.Y);
        SelectionRect.BorderBrush = new SolidColorBrush(Color.Parse(crossing ? "#2E9E5B" : "#0696D7"));
        SelectionRect.Background = new SolidColorBrush(Color.Parse(crossing ? "#222E9E5B" : "#220696D7"));
        SelectionRect.BorderThickness = new Thickness(1);
        SelectionRect.IsVisible = true;
    }

    void FinishBoxSelection(Point p, KeyModifiers mods)
    {
        _boxSelecting = false;
        SelectionRect.IsVisible = false;
        bool crossing = p.X < _downPos.X;
        float x0 = (float)Math.Min(p.X, _downPos.X), x1 = (float)Math.Max(p.X, _downPos.X);
        float y0 = (float)Math.Min(p.Y, _downPos.Y), y1 = (float)Math.Max(p.Y, _downPos.Y);
        bool In(Vector2 q) => q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1;
        bool add = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Shift);
        if (_mode == Mode.Sketch && ActiveSketch is { } sk)
        {
            if (!add) Scene.SelectedEntities.Clear();
            var f = new Frame(sk.Plane);
            for (int i = 0; i < sk.Entities.Count; i++)
            {
                var pts = SketchGeom.Polylines(sk.Entities[i]).SelectMany(pl => pl).Select(q => Cam.Project(f.To3(q.X, q.Y))).ToList();
                if (pts.Count == 0) continue;
                if (crossing ? pts.Any(In) : pts.All(In)) Scene.SelectedEntities.Add(i);
            }
        }
        else if (_mode == Mode.Idle)
        {
            if (!add) { Scene.SelectedBodies.Clear(); Scene.SelectedFaces.Clear(); Scene.SelectedEdges.Clear(); }
            foreach (var b in Scene.Bodies.Where(b => !Scene.HiddenBodies.Contains(b.Index)))
            {
                var corners = new List<Vector2>();
                for (int i = 0; i < 8; i++)
                    corners.Add(Cam.Project(new Vector3((float)b.BBox[(i & 1) == 0 ? 0 : 3], (float)b.BBox[(i & 2) == 0 ? 1 : 4], (float)b.BBox[(i & 4) == 0 ? 2 : 5])));
                if (crossing ? corners.Any(In) || BoxOverlap(corners, x0, y0, x1, y1) : corners.All(In)) Scene.SelectedBodies.Add(b.Index);
            }
        }
        Viewport.Invalidate();
    }

    static bool BoxOverlap(List<Vector2> pts, float x0, float y0, float x1, float y1)
    {
        float minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X), minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
        return minX <= x1 && maxX >= x0 && minY <= y1 && maxY >= y0;
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    void Hover(float x, float y)
    {
        _cursorPx = new Vector2(x, y);
        Scene.HoverFace = null; Scene.HoverEdge = null; Scene.HoverProfile = null; Scene.HoverEntity = null; Scene.HoverPlane = null;
        switch (_mode)
        {
            case Mode.Idle:
                var edge = Picking.PickEdge(Scene, Cam, x, y);
                if (edge != null) Scene.HoverEdge = edge;
                else Scene.HoverFace = Picking.PickFace(Scene, Cam, x, y)?.face;
                break;
            case Mode.PickSketchPlane:
                Scene.HoverPlane = Picking.PickOriginPlane(Cam, x, y);
                if (Scene.HoverPlane == null && Picking.PickFace(Scene, Cam, x, y) is { } hit && IsPlanar(hit.face)) Scene.HoverFace = hit.face;
                break;
            case Mode.Sketch:
                SketchHover(x, y);
                break;
            case Mode.Command:
                CommandHover(x, y);
                break;
        }
        Viewport.Invalidate();
    }

    async Task Click(float x, float y, KeyModifiers mods)
    {
        bool add = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Shift);
        switch (_mode)
        {
            case Mode.Idle:
            {
                var edge = Picking.PickEdge(Scene, Cam, x, y);
                var face = edge == null ? Picking.PickFace(Scene, Cam, x, y)?.face : null;
                if (!add) { Scene.SelectedEdges.Clear(); Scene.SelectedFaces.Clear(); Scene.SelectedBodies.Clear(); }
                if (edge is { } er) Toggle(Scene.SelectedEdges, er);
                else if (face is { } fr) Toggle(Scene.SelectedFaces, fr);
                Viewport.Invalidate();
                break;
            }
            case Mode.PickSketchPlane:
            {
                var plane = Picking.PickOriginPlane(Cam, x, y) switch { "XY" => Plane.XY, "XZ" => Plane.XZ, "YZ" => Plane.YZ, _ => null };
                if (plane == null && Picking.PickFace(Scene, Cam, x, y) is { } hit && IsPlanar(hit.face)) plane = PlaneOfFace(hit.face);
                if (plane != null) await StartSketch(plane);
                break;
            }
            case Mode.Sketch:
                await SketchClick(x, y, add);
                break;
            case Mode.Command:
                await CommandClick(x, y, add);
                break;
        }
    }

    static void Toggle<T>(HashSet<T> set, T v) { if (!set.Remove(v)) set.Add(v); }

    bool IsPlanar(FaceRef f)
    {
        var b = Scene.Bodies.FirstOrDefault(b => b.Index == f.Body);
        return b != null && f.Face * 7 < b.FaceInfo.Length && b.FaceInfo[f.Face * 7] > 0.5f;
    }

    Plane PlaneOfFace(FaceRef f)
    {
        var b = Scene.Bodies.First(b => b.Index == f.Body);
        var i = f.Face * 7;
        var c = new Vector3(b.FaceInfo[i + 1], b.FaceInfo[i + 2], b.FaceInfo[i + 3]);
        var n = Vector3.Normalize(new Vector3(b.FaceInfo[i + 4], b.FaceInfo[i + 5], b.FaceInfo[i + 6]));
        // 법선에 가장 수직인 월드 축을 스케치 X 방향으로
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var x = axes.OrderBy(a => MathF.Abs(Vector3.Dot(a, n))).First();
        x = Vector3.Normalize(x - n * Vector3.Dot(x, n));
        return new Plane { Origin = [c.X, c.Y, c.Z], Normal = [n.X, n.Y, n.Z], Xdir = [x.X, x.Y, x.Z] };
    }

    // ------------------------------------------------------------------ 스케치 시작/종료

    void StartSketchPick()
    {
        CancelCommand();
        _mode = Mode.PickSketchPlane;
        Scene.ShowOriginPlanes = true;
        Scene.ClearSelection();
        ShowToast("스케치할 평면 또는 평평한 면을 선택하십시오.", 6);
        Viewport.Invalidate();
    }

    void EndSketchPick()
    {
        _mode = Mode.Idle;
        Scene.ShowOriginPlanes = false;
        Viewport.Invalidate();
    }

    async Task StartSketch(Plane plane)
    {
        Scene.ShowOriginPlanes = false;
        Snapshot();
        var sk = new SketchFeature { Name = _doc.NextName("Sketch"), Plane = plane };
        InsertFeature(sk);
        _sketchIsNew = true;
        _dirty = true; UpdateTitle();
        await EnterSketch(sk.Id);
    }

    public async Task EnterSketch(string id)
    {
        if (_doc.Find(id) is not SketchFeature sk) return;
        _sketchId = id;
        _mode = Mode.Sketch;
        _clicks.Clear(); Scene.Preview.Clear(); Scene.ClearSelection();
        _hiddenSketches.Remove(id);
        BuildToolbar();
        SetSketchTool(SketchTool.Line);
        SketchPalette.IsVisible = true;
        CommandPanel.IsVisible = false;
        var f = new Frame(sk.Plane);
        // 스케치 평면을 정면에서 보도록 카메라 회전
        var t2 = f.To2(Cam.Target);
        Cam.LookFrom(f.N, f.To3(t2.X, t2.Y));
        await Regenerate();
    }

    void ExitSketch(bool regen)
    {
        if (_mode != Mode.Sketch) return;
        var sk = ActiveSketch;
        if (sk != null && _sketchIsNew && sk.Entities.Count == 0)
        {
            // 아무것도 그리지 않은 새 스케치는 남기지 않는다
            _doc.Features.Remove(sk);
            if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
        }
        _mode = Mode.Idle;
        _sketchId = null; _sketchIsNew = false;
        _clicks.Clear(); Scene.Preview.Clear(); Scene.SnapMark = null;
        SketchPalette.IsVisible = false;
        DimBox.IsVisible = false;
        BuildToolbar();
        if (regen) _ = Regenerate();
    }

    void SetSketchTool(SketchTool t)
    {
        _tool = t;
        _clicks.Clear(); Scene.Preview.Clear();
        DimBox.IsVisible = false;
        UpdateSketchToolButtons();
        SketchHint.Text = t switch
        {
            SketchTool.Line => "점을 클릭해 선을 이어 그립니다. 시작점을 클릭하면 닫히고, Esc 또는 오른쪽 클릭으로 끝냅니다. 숫자를 입력하면 길이를 지정합니다.",
            SketchTool.Rect => "두 모서리를 클릭합니다. '가로,세로' 를 입력할 수 있습니다.",
            SketchTool.CenterRect => "중심을 클릭한 뒤 모서리를 클릭합니다. '가로,세로' 를 입력할 수 있습니다.",
            SketchTool.Circle => "중심을 클릭한 뒤 원 위의 점을 클릭합니다. 숫자 입력 = 지름.",
            SketchTool.Arc => "시작점, 끝점, 호 위의 점을 차례로 클릭합니다.",
            SketchTool.PolygonCircumscribed or SketchTool.PolygonInscribed => $"중심을 클릭한 뒤 크기를 클릭합니다. 변 {_polygonSides}개 (숫자 입력 = {(t == SketchTool.PolygonInscribed ? "외접원" : "내접원")} 지름).",
            SketchTool.Point => "점을 클릭합니다.",
            SketchTool.Dimension => "치수를 줄 선·원·호·직사각형을 클릭한 뒤 값을 입력하고 Enter.",
            _ => "클릭하거나 끌어서(왼→오 창, 오→왼 교차) 선택합니다. Delete 로 지우고 X 로 보통/구성을 바꿉니다.",
        };
        Viewport.Invalidate();
    }

    void UpdateSketchToolButtons()
    {
        foreach (var (k, b) in _sketchToolButtons) b.Classes.Set("checked", k == _tool);
        LinetypeButton.Content = _constructionMode ? "구성" : "보통";
    }

    /// X: 선택한 선을 보통 ↔ 구성으로. 선택이 없으면 새로 그릴 선의 선종류를 바꾼다.
    void ToggleConstruction()
    {
        var sk = ActiveSketch;
        if (sk == null) return;
        if (Scene.SelectedEntities.Count == 0)
        {
            _constructionMode = !_constructionMode;
            UpdateSketchToolButtons();
            ShowToast(_constructionMode ? "선종류: 구성" : "선종류: 보통", 2);
            return;
        }
        Snapshot();
        foreach (var i in Scene.SelectedEntities.ToList())
        {
            if (i >= sk.Entities.Count) continue;
            sk.Entities[i] = sk.Entities[i] switch
            {
                LineEntity l => new ConstructionEntity { A = l.A, B = l.B },
                ConstructionEntity c => new LineEntity { A = c.A, B = c.B },
                var e => e,
            };
        }
        _dirty = true; UpdateTitle();
        _ = Regenerate();
    }

    // ------------------------------------------------------------------ 스냅 / 미리보기

    float SnapStep()
    {
        float target = Cam.PixelSize * 40;
        float p = MathF.Pow(10, MathF.Floor(MathF.Log10(target)));
        foreach (var m in new[] { 1f, 2f, 5f, 10f }) if (p * m >= target) return p * m / 10;
        return p;
    }

    Vector2? CursorOnSketch(float x, float y)
    {
        var sk = ActiveSketch;
        if (sk == null) return null;
        var f = new Frame(sk.Plane);
        var (o, d) = Cam.Ray(x, y);
        var p = f.Hit(o, d) ?? f.Hit(o, -d);
        if (p == null) return null;
        var q = p.Value;
        Scene.SnapMark = null;
        if (SnapPoints.IsChecked == true)
        {
            float tol = 8 * Cam.PixelSize;
            var cands = sk.Entities.SelectMany(SketchGeom.SnapPoints).Concat(_clicks).Append(Vector2.Zero);
            foreach (var c in cands)
                if (Vector2.Distance(c, q) < tol) { Scene.SnapMark = c; return c; }
        }
        if (SnapGrid.IsChecked == true)
        {
            float s = SnapStep();
            q = new Vector2(MathF.Round(q.X / s) * s, MathF.Round(q.Y / s) * s);
        }
        return q;
    }

    void SketchHover(float x, float y)
    {
        var p = CursorOnSketch(x, y);
        if (p == null) return;
        _cursor2 = p.Value;
        if (_tool is SketchTool.Select or SketchTool.Dimension && ActiveSketch is { } sk)
            Scene.HoverEntity = Picking.PickEntity(new SketchSnapshot(sk.Entities), Cam, _cursor2);
        UpdatePreview();
    }

    static double[] D(Vector2 v) => [Math.Round(v.X, 6), Math.Round(v.Y, 6)];

    List<Entity> BuildEntities(IReadOnlyList<Vector2> pts)
    {
        var o = new List<Entity>();
        switch (_tool)
        {
            case SketchTool.Line when pts.Count >= 2 && Vector2.Distance(pts[0], pts[1]) > 1e-6f:
                o.Add(_constructionMode ? new ConstructionEntity { A = D(pts[0]), B = D(pts[1]) } : new LineEntity { A = D(pts[0]), B = D(pts[1]) });
                break;
            case SketchTool.Construction when pts.Count >= 2 && Vector2.Distance(pts[0], pts[1]) > 1e-6f:
                o.Add(new ConstructionEntity { A = D(pts[0]), B = D(pts[1]) });
                break;
            case SketchTool.Rect when pts.Count >= 2 && MathF.Abs(pts[0].X - pts[1].X) > 1e-6f && MathF.Abs(pts[0].Y - pts[1].Y) > 1e-6f:
                o.Add(new RectEntity { A = D(pts[0]), B = D(pts[1]) });
                break;
            case SketchTool.CenterRect when pts.Count >= 2 && MathF.Abs(pts[0].X - pts[1].X) > 1e-6f && MathF.Abs(pts[0].Y - pts[1].Y) > 1e-6f:
                o.Add(new RectEntity { A = D(2 * pts[0] - pts[1]), B = D(pts[1]) });
                break;
            case SketchTool.Circle when pts.Count >= 2 && Vector2.Distance(pts[0], pts[1]) > 1e-6f:
                o.Add(new CircleEntity { C = D(pts[0]), R = Math.Round(Vector2.Distance(pts[0], pts[1]), 6) });
                break;
            case SketchTool.Arc when pts.Count >= 3:
                var a = new ArcEntity { A = D(pts[0]), B = D(pts[1]), M = D(pts[2]) };
                if (SketchGeom.ArcParams(a.A, a.M, a.B, out _, out _, out _, out _, out _)) o.Add(a);
                break;
            case SketchTool.PolygonCircumscribed or SketchTool.PolygonInscribed when pts.Count >= 2 && Vector2.Distance(pts[0], pts[1]) > 1e-6f:
            {
                // 외접: 커서가 변의 중점(내접원 반지름), 내접: 커서가 꼭짓점(외접원 반지름)
                int n = _polygonSides;
                var c = pts[0]; var v = pts[1] - c;
                float r = v.Length(), ang = MathF.Atan2(v.Y, v.X);
                if (_tool == SketchTool.PolygonCircumscribed) { r /= MathF.Cos(MathF.PI / n); ang += MathF.PI / n; }
                var corners = Enumerable.Range(0, n).Select(i => c + r * new Vector2(MathF.Cos(ang + i * MathF.Tau / n), MathF.Sin(ang + i * MathF.Tau / n))).ToList();
                for (int i = 0; i < n; i++) o.Add(new LineEntity { A = D(corners[i]), B = D(corners[(i + 1) % n]) });
                break;
            }
            case SketchTool.Point when pts.Count >= 1:
                o.Add(new PointEntity { P = D(pts[0]) });
                break;
        }
        return o;
    }

    Entity? BuildEntity(IReadOnlyList<Vector2> pts) => BuildEntities(pts).FirstOrDefault();

    void UpdatePreview()
    {
        Scene.Preview.Clear();
        if (_clicks.Count == 0) return;
        var pts = _clicks.Append(_cursor2).ToList();
        if (_tool == SketchTool.Arc && pts.Count == 2) { Scene.Preview.Add(new LineEntity { A = D(pts[0]), B = D(pts[1]) }); return; }
        Scene.Preview.AddRange(BuildEntities(_tool == SketchTool.Line ? [_clicks[^1], _cursor2] : pts));
    }

    int NeededClicks => _tool switch { SketchTool.Arc => 3, SketchTool.Select or SketchTool.Dimension => 0, SketchTool.Point => 1, _ => 2 };
    Vector2? _chainStart;
    int _chainSegs;

    async Task SketchClick(float x, float y, bool add)
    {
        var sk = ActiveSketch;
        if (sk == null) return;
        if (_tool is SketchTool.Select or SketchTool.Dimension)
        {
            var hit = Picking.PickEntity(new SketchSnapshot(sk.Entities), Cam, _cursor2);
            if (_tool == SketchTool.Dimension)
            {
                Scene.SelectedEntities.Clear();
                if (hit is { } dh)
                {
                    Scene.SelectedEntities.Add(dh);
                    DimBox.IsVisible = true;
                    DimBox.Margin = new Thickness(_cursorPx.X + 16, _cursorPx.Y + 16, 0, 0);
                    DimInput.Text = CurrentDimension(sk.Entities[dh]);
                    DimInput.Focus();
                    DimInput.SelectAll();
                }
                Viewport.Invalidate();
                return;
            }
            if (!add) Scene.SelectedEntities.Clear();
            if (hit is { } h) Toggle(Scene.SelectedEntities, h);
            Viewport.Invalidate();
            return;
        }
        var p = CursorOnSketch(x, y);
        if (p == null) return;
        await AddPoint(p.Value);
    }

    async Task AddPoint(Vector2 p)
    {
        _clicks.Add(p);
        if (_tool == SketchTool.Line)
        {
            if (_clicks.Count == 1) { _chainStart = p; _chainSegs = 0; UpdatePreview(); Viewport.Invalidate(); return; }
            var e = BuildEntity([_clicks[^2], _clicks[^1]]);
            if (e != null) { await AddEntity(e); _chainSegs++; }
            // 시작점으로 돌아오면 닫고 체인 종료
            if (_chainStart is { } cs && Vector2.Distance(cs, p) < 1e-5f && _chainSegs >= 2) { EndChain(); return; }
            var last = _clicks[^1]; _clicks.Clear(); _clicks.Add(last);
            UpdatePreview();
            return;
        }
        if (_clicks.Count < NeededClicks) { UpdatePreview(); Viewport.Invalidate(); return; }
        var ents = BuildEntities(_clicks);
        _clicks.Clear();
        Scene.Preview.Clear();
        if (ents.Count > 0) await AddEntities(ents);
    }

    Task AddEntity(Entity e) => AddEntities([e]);

    async Task AddEntities(List<Entity> es)
    {
        var sk = ActiveSketch;
        if (sk == null) return;
        Snapshot();
        sk.Entities.AddRange(es);
        _dirty = true; UpdateTitle();
        DimBox.IsVisible = false;
        await Regenerate();
    }

    void EndChain()
    {
        _clicks.Clear(); _chainStart = null; _chainSegs = 0; Scene.Preview.Clear(); DimBox.IsVisible = false;
        Viewport.Invalidate();
    }

    void DeleteSelectedEntities()
    {
        var sk = ActiveSketch;
        if (sk == null || Scene.SelectedEntities.Count == 0) { ShowToast("삭제할 스케치 요소를 선택 도구로 선택하십시오."); return; }
        Snapshot();
        foreach (var i in Scene.SelectedEntities.OrderByDescending(i => i)) if (i < sk.Entities.Count) sk.Entities.RemoveAt(i);
        Scene.SelectedEntities.Clear();
        _dirty = true; UpdateTitle();
        _ = Regenerate();
    }

    // ------------------------------------------------------------------ 키보드 / 치수 입력

    void SketchEscape()
    {
        if (_clicks.Count > 0) EndChain();
        else if (_tool != SketchTool.Select) SetSketchTool(SketchTool.Select);
        else { Scene.SelectedEntities.Clear(); Viewport.Invalidate(); }
    }

    /// 그리는 중 숫자 입력 → 치수 상자 (Fusion 의 동적 입력)
    bool SketchDigitKey(KeyEventArgs e)
    {
        var ch = KeyChar(e.Key);
        if (ch == null) return false;
        bool drawing = _clicks.Count > 0 && _tool is not (SketchTool.Arc or SketchTool.Select or SketchTool.Point);
        bool dimension = _tool == SketchTool.Dimension && Scene.SelectedEntities.Count > 0;
        if (!drawing && !dimension) return false;
        DimBox.IsVisible = true;
        DimBox.Margin = new Thickness(_cursorPx.X + 16, _cursorPx.Y + 16, 0, 0);
        DimInput.Text = ch;
        DimInput.Focus();
        DimInput.CaretIndex = DimInput.Text.Length;
        return true;
    }

    static string? KeyChar(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((int)(k - Key.NumPad0)).ToString(),
        Key.OemPeriod or Key.Decimal => ".",
        _ => null,
    };

    void HookSketchPalette()
    {
        FinishSketchButton.Click += (_, _) => ExitSketch(true);
        LinetypeButton.Click += (_, _) => ToggleConstruction();
        PaletteLookAt.Content = Icons.Make("lookat", 16);
        PaletteLookAt.Click += (_, _) => LookAtSelection();
        SketchGrid.IsCheckedChanged += (_, _) => { Scene.ShowGrid = SketchGrid.IsChecked == true; Viewport.Invalidate(); };
        ShowPoints.IsCheckedChanged += (_, _) => { Scene.ShowSketchPoints = ShowPoints.IsChecked == true; Viewport.Invalidate(); };
        ShowProfile.IsCheckedChanged += (_, _) => { foreach (var s in Scene.Sketches) if (ShowProfile.IsChecked == false) s.Profiles = []; _ = Regenerate(); };
        DimInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { DimBox.IsVisible = false; Viewport.Focus(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var parts = (DimInput.Text ?? "").Split([',', 'x', 'X', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            DimBox.IsVisible = false;
            Viewport.Focus();
            if (parts.Length == 0 || parts.Any(double.IsNaN)) return;
            if (_tool == SketchTool.Dimension) { await ApplyDimension(parts); return; }
            if (_clicks.Count == 0) return;
            var a = _clicks[^1];
            var dir = _cursor2 - a;
            if (dir.LengthSquared() < 1e-9f) dir = Vector2.UnitX;
            Vector2 target = _tool switch
            {
                SketchTool.Line or SketchTool.Construction => a + Vector2.Normalize(dir) * (float)parts[0],
                SketchTool.Circle => a + Vector2.Normalize(dir) * (float)(parts[0] / 2),
                SketchTool.CenterRect => a + new Vector2(MathF.Sign(dir.X == 0 ? 1 : dir.X) * (float)parts[0] / 2, MathF.Sign(dir.Y == 0 ? 1 : dir.Y) * (float)(parts.Length > 1 ? parts[1] : parts[0]) / 2),
                SketchTool.PolygonCircumscribed or SketchTool.PolygonInscribed => a + Vector2.Normalize(dir) * (float)(parts[0] / 2),
                SketchTool.Rect => a + new Vector2(MathF.Sign(dir.X == 0 ? 1 : dir.X) * (float)parts[0], MathF.Sign(dir.Y == 0 ? 1 : dir.Y) * (float)(parts.Length > 1 ? parts[1] : parts[0])),
                _ => _cursor2,
            };
            await AddPoint(target);
        };
    }

    /// 스케치 치수: 선택 요소의 현재 크기 문자열
    static string CurrentDimension(Entity e) => e switch
    {
        LineEntity l => Math.Round(Math.Sqrt(Math.Pow(l.B[0] - l.A[0], 2) + Math.Pow(l.B[1] - l.A[1], 2)), 3).ToString(CultureInfo.InvariantCulture),
        ConstructionEntity c => Math.Round(Math.Sqrt(Math.Pow(c.B[0] - c.A[0], 2) + Math.Pow(c.B[1] - c.A[1], 2)), 3).ToString(CultureInfo.InvariantCulture),
        CircleEntity c => Math.Round(c.R * 2, 3).ToString(CultureInfo.InvariantCulture),
        RectEntity r => $"{Math.Round(Math.Abs(r.B[0] - r.A[0]), 3).ToString(CultureInfo.InvariantCulture)},{Math.Round(Math.Abs(r.B[1] - r.A[1]), 3).ToString(CultureInfo.InvariantCulture)}",
        _ => "",
    };

    /// 스케치 치수 값 적용 (선: 길이, 원: 지름, 직사각형: 가로,세로)
    async Task ApplyDimension(double[] v)
    {
        var sk = ActiveSketch;
        if (sk == null || Scene.SelectedEntities.Count == 0) return;
        int i = Scene.SelectedEntities.First();
        if (i >= sk.Entities.Count || v[0] <= 0) return;
        Snapshot();
        static double[] Along(double[] a, double[] b, double len)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], l = Math.Sqrt(dx * dx + dy * dy);
            return l < 1e-12 ? [a[0] + len, a[1]] : [Math.Round(a[0] + dx / l * len, 6), Math.Round(a[1] + dy / l * len, 6)];
        }
        sk.Entities[i] = sk.Entities[i] switch
        {
            LineEntity l => new LineEntity { A = l.A, B = Along(l.A, l.B, v[0]) },
            ConstructionEntity c => new ConstructionEntity { A = c.A, B = Along(c.A, c.B, v[0]) },
            CircleEntity c => new CircleEntity { C = c.C, R = v[0] / 2 },
            RectEntity r => new RectEntity { A = r.A, B = [r.A[0] + Math.Sign(r.B[0] - r.A[0] == 0 ? 1 : r.B[0] - r.A[0]) * v[0], r.A[1] + Math.Sign(r.B[1] - r.A[1] == 0 ? 1 : r.B[1] - r.A[1]) * (v.Length > 1 ? v[1] : v[0])] },
            var e => e,
        };
        _dirty = true; UpdateTitle();
        await Regenerate();
    }

    // ------------------------------------------------------------------ 오버레이 (치수 표시, 평면 이름)

    void UpdateOverlay()
    {
        Overlay.Children.Clear();
        void Label(string text, Vector2 at, string color = "#1D4FB8", bool box = true)
        {
            var tb = new TextBlock { Text = text, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse(color)) };
            Control c = box ? new Border { Child = tb, Background = new SolidColorBrush(Color.Parse("#E6FFFFFF")), BorderBrush = new SolidColorBrush(Color.Parse("#9FB6DC")), BorderThickness = new Thickness(1), Padding = new Thickness(4, 1), CornerRadius = new CornerRadius(2) } : tb;
            Canvas.SetLeft(c, at.X); Canvas.SetTop(c, at.Y);
            Overlay.Children.Add(c);
        }
        if (_mode == Mode.PickSketchPlane)
        {
            float size = Picking.OriginPlaneSize * Cam.Distance / 200;
            foreach (var (name, pl) in new[] { ("XY", Plane.XY), ("XZ", Plane.XZ), ("YZ", Plane.YZ) })
            {
                var f = new Frame(pl);
                var p = Cam.Project(f.To3(size * 0.5, size * 0.5));
                if (!float.IsNaN(p.X)) Label(name, p, "#8A4B00");
            }
        }
        if (_mode == Mode.Sketch && _clicks.Count > 0 && ActiveSketch is { } sk)
        {
            var a = _clicks[^1];
            string? text = _tool switch
            {
                SketchTool.Line or SketchTool.Construction => $"{Vector2.Distance(a, _cursor2):0.###} mm  ∠{MathF.Atan2(_cursor2.Y - a.Y, _cursor2.X - a.X) * 180 / MathF.PI:0.#}°",
                SketchTool.Rect => $"{MathF.Abs(_cursor2.X - a.X):0.###} × {MathF.Abs(_cursor2.Y - a.Y):0.###} mm",
                SketchTool.CenterRect => $"{2 * MathF.Abs(_cursor2.X - a.X):0.###} × {2 * MathF.Abs(_cursor2.Y - a.Y):0.###} mm",
                SketchTool.PolygonCircumscribed or SketchTool.PolygonInscribed => $"⌀ {2 * Vector2.Distance(a, _cursor2):0.###} mm  ({_polygonSides}변)",
                SketchTool.Circle => $"⌀ {2 * Vector2.Distance(a, _cursor2):0.###} mm",
                SketchTool.Arc => _clicks.Count == 1 ? $"{Vector2.Distance(a, _cursor2):0.###} mm" : null,
                _ => null,
            };
            if (text != null && !DimBox.IsVisible) Label(text, _cursorPx + new Vector2(16, 14));
        }
        if (_mode == Mode.Sketch && ActiveSketch is { } s2)
        {
            var f = new Frame(s2.Plane);
            var p = Cam.Project(f.To3(_cursor2.X, _cursor2.Y));
            if (!float.IsNaN(p.X) && _clicks.Count == 0) Label($"{_cursor2.X:0.##}, {_cursor2.Y:0.##}", _cursorPx + new Vector2(14, -22), "#5B616B", false);
        }
    }
}
