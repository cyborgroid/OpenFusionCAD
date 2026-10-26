using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Sketching;

namespace OpenFusionCAD.Viewport;

/// CPU 기반 선택: 면(광선-삼각형), 모서리(화면 거리), 스케치 엔티티/프로파일, 원점 평면
public static class Picking
{
    public static (FaceRef face, float t, Vector3 point)? PickFace(Scene s, Camera cam, float x, float y)
    {
        var (o, d) = cam.Ray(x, y);
        (FaceRef, float, Vector3)? best = null;
        foreach (var b in s.Bodies)
        {
            if (s.HiddenBodies.Contains(b.Index)) continue;
            var p = b.Positions; var idx = b.Indices;
            for (int f = 0; f < b.FaceCount; f++)
            {
                for (uint tri = b.FaceStart[f]; tri < b.FaceStart[f + 1]; tri++)
                {
                    int i0 = (int)idx[tri * 3] * 3, i1 = (int)idx[tri * 3 + 1] * 3, i2 = (int)idx[tri * 3 + 2] * 3;
                    var v0 = new Vector3(p[i0], p[i0 + 1], p[i0 + 2]);
                    var v1 = new Vector3(p[i1], p[i1 + 1], p[i1 + 2]);
                    var v2 = new Vector3(p[i2], p[i2 + 1], p[i2 + 2]);
                    if (RayTri(o, d, v0, v1, v2, out float t) && (best == null || t < best.Value.Item2))
                        best = (new FaceRef(b.Index, f), t, o + d * t);
                }
            }
        }
        return best;
    }

    static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a; var e2 = c - a;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1 / det;
        var tv = o - a;
        float u = Vector3.Dot(tv, pv) * inv;
        if (u < 0 || u > 1) return false;
        var qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, qv) * inv;
        return t > 1e-6f;
    }

    /// 커서에서 tol 픽셀 이내의 가장 가까운 모서리. 가려진 모서리는 제외한다.
    public static EdgeRef? PickEdge(Scene s, Camera cam, float x, float y, float tol = 7)
    {
        var hit = PickFace(s, cam, x, y);
        float occl = hit.HasValue ? cam.Depth(hit.Value.point) : float.MaxValue;
        var q = new Vector2(x, y);
        EdgeRef? best = null;
        float bestD = tol;
        foreach (var b in s.Bodies)
        {
            if (s.HiddenBodies.Contains(b.Index)) continue;
            var e = b.Edges;
            for (int k = 0; k < b.EdgeIds.Length; k++)
            {
                var a3 = new Vector3(e[k * 6], e[k * 6 + 1], e[k * 6 + 2]);
                var b3 = new Vector3(e[k * 6 + 3], e[k * 6 + 4], e[k * 6 + 5]);
                var a2 = cam.Project(a3); var b2 = cam.Project(b3);
                if (float.IsNaN(a2.X) || float.IsNaN(b2.X)) continue;
                float dd = SketchGeom.DistToSeg(q, a2, b2);
                if (dd >= bestD) continue;
                // 앞면보다 확실히 뒤에 있는 모서리는 가려진 것
                float depth = cam.Depth((a3 + b3) / 2);
                if (depth > occl + cam.PixelSize * 4 + 0.05f * cam.Distance / 100) continue;
                bestD = dd;
                best = new EdgeRef(b.Index, (int)b.EdgeIds[k]);
            }
        }
        return best;
    }

    /// 활성 스케치에서 커서 근처 엔티티
    public static int? PickEntity(SketchSnapshot sk, Camera cam, Vector2 q2, float tolPx = 6)
    {
        float tol = tolPx * cam.PixelSize;
        int? best = null; float bestD = tol;
        for (int i = 0; i < sk.Entities.Count; i++)
            foreach (var pl in SketchGeom.Polylines(sk.Entities[i]))
            {
                float d = SketchGeom.DistToPolyline(q2, pl);
                if (d < bestD) { bestD = d; best = i; }
            }
        return best;
    }

    public static int? PickProfile(SketchView sv, Vector2 q2)
    {
        // 겹치는 경우 가장 작은 영역 우선
        int? best = null; double bestArea = double.MaxValue;
        for (int i = 0; i < sv.Profiles.Count; i++)
        {
            var p = sv.Profiles[i];
            if (!SketchGeom.PointInPolygon(q2, p.Outer)) continue;
            if (p.Holes.Any(h => SketchGeom.PointInPolygon(q2, h))) continue;
            if (p.Area < bestArea) { bestArea = p.Area; best = i; }
        }
        return best;
    }

    public const float OriginPlaneSize = 30;

    /// 원점 평면(XY/XZ/YZ) 선택
    public static string? PickOriginPlane(Camera cam, float x, float y)
    {
        var (o, d) = cam.Ray(x, y);
        float size = OriginPlaneSize * cam.Distance / 200;
        string? best = null; float bestT = float.MaxValue;
        foreach (var (name, plane) in new[] { ("XY", Plane.XY), ("XZ", Plane.XZ), ("YZ", Plane.YZ) })
        {
            var f = new Frame(plane);
            float den = Vector3.Dot(d, f.N);
            if (MathF.Abs(den) < 1e-6f) continue;
            float t = Vector3.Dot(f.O - o, f.N) / den;
            if (t <= 0 || t >= bestT) continue;
            var p2 = f.To2(o + d * t);
            if (p2.X >= 0 && p2.Y >= 0 && p2.X <= size && p2.Y <= size) { best = name; bestT = t; }
        }
        return best;
    }
}

/// 스케치 편집 중인 엔티티 목록의 읽기 전용 뷰
public readonly record struct SketchSnapshot(List<Entity> Entities);
