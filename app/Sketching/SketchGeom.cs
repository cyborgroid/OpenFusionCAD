using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;

namespace OpenFusionCAD.Sketching;

/// 스케치 평면 좌표계 (커널 regen.rs 의 Frame 과 동일한 규칙)
public readonly struct Frame
{
    public readonly Vector3 O, X, Y, N;

    public Frame(Plane p)
    {
        N = Vector3.Normalize(V(p.Normal));
        var x = V(p.Xdir);
        X = Vector3.Normalize(x - N * Vector3.Dot(x, N));
        Y = Vector3.Cross(N, X);
        O = V(p.Origin);
    }

    static Vector3 V(double[] a) => new((float)a[0], (float)a[1], (float)a[2]);

    public Vector3 To3(double x, double y) => O + X * (float)x + Y * (float)y;
    public Vector3 To3(double[] p) => To3(p[0], p[1]);
    public Vector2 To2(Vector3 p) { var d = p - O; return new Vector2(Vector3.Dot(d, X), Vector3.Dot(d, Y)); }

    /// 광선과 평면 교점 (평면 2D 좌표)
    public Vector2? Hit(Vector3 ro, Vector3 rd)
    {
        float den = Vector3.Dot(rd, N);
        if (MathF.Abs(den) < 1e-6f) return null;
        float t = Vector3.Dot(O - ro, N) / den;
        return t < 0 ? null : To2(ro + rd * t);
    }
}

public static class SketchGeom
{
    /// 3점 호 → 중심, 반지름, 시작각, 부호 있는 회전각 (커널 sketch.rs 와 동일)
    public static bool ArcParams(double[] a, double[] m, double[] b, out double cx, out double cy, out double r, out double t0, out double sweep)
    {
        cx = cy = r = t0 = sweep = 0;
        double d = 2 * (a[0] * (m[1] - b[1]) + m[0] * (b[1] - a[1]) + b[0] * (a[1] - m[1]));
        if (Math.Abs(d) < 1e-12) return false;
        double Sq(double[] p) => p[0] * p[0] + p[1] * p[1];
        cx = (Sq(a) * (m[1] - b[1]) + Sq(m) * (b[1] - a[1]) + Sq(b) * (a[1] - m[1])) / d;
        cy = (Sq(a) * (b[0] - m[0]) + Sq(m) * (a[0] - b[0]) + Sq(b) * (m[0] - a[0])) / d;
        r = Math.Sqrt((a[0] - cx) * (a[0] - cx) + (a[1] - cy) * (a[1] - cy));
        double x0 = cx, y0 = cy;
        double Ang(double[] p) => Math.Atan2(p[1] - y0, p[0] - x0);
        double ta = Ang(a), tm = Ang(m), tb = Ang(b);
        double Norm(double x) => ((x % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        bool ccw = Norm(tm - ta) < Norm(tb - ta);
        sweep = ccw ? Norm(tb - ta) : -Norm(ta - tb);
        t0 = ta;
        return true;
    }

    /// 엔티티 → 2D 폴리라인 목록 (사각형은 4개 선분)
    public static IEnumerable<List<Vector2>> Polylines(Entity e)
    {
        static Vector2 P(double[] p) => new((float)p[0], (float)p[1]);
        switch (e)
        {
            case LineEntity l: yield return [P(l.A), P(l.B)]; break;
            case ConstructionEntity c: yield return [P(c.A), P(c.B)]; break;
            case PointEntity pt:
            {
                // 점은 작은 십자로 표시/선택
                var q = P(pt.P);
                yield return [q - new Vector2(0.4f, 0), q + new Vector2(0.4f, 0)];
                yield return [q - new Vector2(0, 0.4f), q + new Vector2(0, 0.4f)];
                break;
            }
            case RectEntity r:
                var p = new[] { P(r.A), new Vector2((float)r.B[0], (float)r.A[1]), P(r.B), new Vector2((float)r.A[0], (float)r.B[1]) };
                yield return [p[0], p[1], p[2], p[3], p[0]];
                break;
            case CircleEntity c:
            {
                var pts = new List<Vector2>();
                for (int i = 0; i <= 96; i++)
                {
                    double t = 2 * Math.PI * i / 96;
                    pts.Add(new Vector2((float)(c.C[0] + c.R * Math.Cos(t)), (float)(c.C[1] + c.R * Math.Sin(t))));
                }
                yield return pts;
                break;
            }
            case ArcEntity a:
                if (ArcParams(a.A, a.M, a.B, out var cx, out var cy, out var rr, out var t0, out var sw))
                {
                    int n = Math.Max(8, (int)(Math.Abs(sw) / (Math.PI / 48)));
                    var pts = new List<Vector2>();
                    for (int i = 0; i <= n; i++)
                    {
                        double t = t0 + sw * i / n;
                        pts.Add(new Vector2((float)(cx + rr * Math.Cos(t)), (float)(cy + rr * Math.Sin(t))));
                    }
                    yield return pts;
                }
                else yield return [P(a.A), P(a.B)];
                break;
        }
    }

    /// 엔티티 끝점/중심점 (스냅 대상)
    public static IEnumerable<Vector2> SnapPoints(Entity e)
    {
        static Vector2 P(double[] p) => new((float)p[0], (float)p[1]);
        switch (e)
        {
            case LineEntity l: yield return P(l.A); yield return P(l.B); yield return (P(l.A) + P(l.B)) / 2; break;
            case ConstructionEntity c: yield return P(c.A); yield return P(c.B); break;
            case PointEntity pt: yield return P(pt.P); break;
            case RectEntity r:
                yield return P(r.A); yield return P(r.B);
                yield return new Vector2((float)r.B[0], (float)r.A[1]); yield return new Vector2((float)r.A[0], (float)r.B[1]);
                break;
            case CircleEntity c: yield return P(c.C); break;
            case ArcEntity a: yield return P(a.A); yield return P(a.B); break;
        }
    }

    public static float DistToPolyline(Vector2 q, List<Vector2> pts)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++) best = MathF.Min(best, DistToSeg(q, pts[i], pts[i + 1]));
        return best;
    }

    public static float DistToSeg(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(q, a + ab * t);
    }

    public static bool PointInPolygon(Vector2 q, IReadOnlyList<double[]> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
            if ((yi > q.Y) != (yj > q.Y) && q.X < (xj - xi) * (q.Y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}
