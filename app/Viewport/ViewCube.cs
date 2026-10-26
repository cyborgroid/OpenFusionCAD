using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenFusionCAD.Viewport;

/// <summary>뷰포트 오른쪽 위의 방향 큐브. 면을 클릭하면 그 방향에서 보도록 카메라를 돌린다.</summary>
public sealed class ViewCube : Control
{
    public Camera? Camera { get; set; }
    public event Action<Vector3>? ViewRequested;
    public event Action? HomeRequested;

    // Z-up: 위 +Z, 앞 -Y, 오른쪽 +X
    static readonly (string label, Vector3 n, Vector3 u)[] Faces =
    [
        ("TOP", Vector3.UnitZ, Vector3.UnitY),
        ("BOTTOM", -Vector3.UnitZ, -Vector3.UnitY),
        ("FRONT", -Vector3.UnitY, Vector3.UnitZ),
        ("BACK", Vector3.UnitY, Vector3.UnitZ),
        ("RIGHT", Vector3.UnitX, Vector3.UnitZ),
        ("LEFT", -Vector3.UnitX, Vector3.UnitZ),
    ];

    string? _hover;
    bool _homeHover;
    const double CubeSize = 34;
    Rect HomeRect => new(4, 4, 18, 18);

    public ViewCube()
    {
        Width = 120; Height = 120;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    List<(string label, Vector3 n, Point[] quad, float depth)> Projected()
    {
        var list = new List<(string, Vector3, Point[], float)>();
        if (Camera == null) return list;
        var view = Camera.View;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2 + 6);
        foreach (var (label, n, u) in Faces)
        {
            var r = Vector3.Cross(u, n);
            var corners = new[] { n + r + u, n - r + u, n - r - u, n + r - u };
            var pts = corners.Select(p =>
            {
                var v = Vector3.TransformNormal(p, view);
                return new Point(c.X + v.X * CubeSize, c.Y - v.Y * CubeSize);
            }).ToArray();
            var nv = Vector3.TransformNormal(n, view);
            list.Add((label, n, pts, nv.Z));
        }
        return list;
    }

    public override void Render(DrawingContext ctx)
    {
        var faces = Projected().OrderBy(f => f.depth).ToList();
        var border = new Pen(new SolidColorBrush(Color.Parse("#8A9099")), 1);
        var typeface = new Typeface("DejaVu Sans", FontStyle.Normal, FontWeight.SemiBold);
        foreach (var (label, n, quad, depth) in faces)
        {
            if (depth <= 0.02f) continue;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(quad[0], true);
                for (int i = 1; i < 4; i++) gc.LineTo(quad[i]);
                gc.EndFigure(true);
            }
            var shade = (byte)(215 + 35 * depth);
            IBrush fill = _hover == label ? new SolidColorBrush(Color.Parse("#9DD3F0")) : new SolidColorBrush(Color.FromRgb(shade, shade, (byte)Math.Min(255, shade + 4)));
            ctx.DrawGeometry(fill, border, g);
            if (depth > 0.35f)
            {
                var center = new Point(quad.Average(p => p.X), quad.Average(p => p.Y));
                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9.5, new SolidColorBrush(Color.Parse("#3A3F47")));
                ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
            }
        }
        // 홈 버튼
        var hr = HomeRect;
        var hpen = new Pen(new SolidColorBrush(Color.Parse(_homeHover ? "#0696D7" : "#5B616B")), 1.4);
        var house = Geometry.Parse($"M{hr.X + 2},{hr.Y + 9} L{hr.X + 9},{hr.Y + 2} L{hr.X + 16},{hr.Y + 9} M{hr.X + 4},{hr.Y + 8} L{hr.X + 4},{hr.Y + 16} L{hr.X + 14},{hr.Y + 16} L{hr.X + 14},{hr.Y + 8}");
        ctx.DrawGeometry(null, hpen, house);
    }

    static bool Inside(Point q, Point[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    string? HitFace(Point p) => Projected().Where(f => f.depth > 0.02f).OrderByDescending(f => f.depth).FirstOrDefault(f => Inside(p, f.quad)).label;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = HitFace(p);
        bool home = HomeRect.Inflate(3).Contains(p);
        if (h != _hover || home != _homeHover) { _hover = h; _homeHover = home; InvalidateVisual(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null; _homeHover = false; InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (HomeRect.Inflate(3).Contains(p)) { HomeRequested?.Invoke(); e.Handled = true; return; }
        var label = HitFace(p);
        if (label == null) return;
        var n = Faces.First(f => f.label == label).n;
        ViewRequested?.Invoke(n);
        e.Handled = true;
    }
}
