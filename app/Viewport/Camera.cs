using System;
using System.Numerics;

namespace OpenFusionCAD.Viewport;

/// <summary>Z-up 궤도 카메라 (yaw: Z축 기준, pitch: 수평면 기준, 단위 rad)</summary>
public sealed class Camera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 200;
    public float Yaw = -MathF.PI / 4; // 홈 뷰: 앞(-Y)·오른쪽(+X)·위가 보이는 방향
    public float Pitch = 0.6f;
    public float Fov = 30f * MathF.PI / 180f;
    public bool Ortho;
    public float Width = 1, Height = 1;

    // 부드러운 시점 전환
    Vector3 _t0, _t1; float _d0, _d1, _y0, _y1, _p0, _p1; double _animStart = -1; const double AnimMs = 350;

    public Vector3 Eye => Target + Distance * Dir;
    public Vector3 Dir => new(MathF.Cos(Pitch) * MathF.Cos(Yaw), MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch));

    Vector3 Up
    {
        get
        {
            // 정면 위/아래에서 볼 때도 안정적인 up 벡터
            var right = Vector3.Normalize(Vector3.Cross(-Dir, Vector3.UnitZ) is var r && r.LengthSquared() > 1e-8f ? r : new Vector3(-MathF.Sin(Yaw), MathF.Cos(Yaw), 0));
            return Vector3.Normalize(Vector3.Cross(right, -Dir));
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Up);

    public Matrix4x4 Projection
    {
        get
        {
            float aspect = Width / MathF.Max(Height, 1);
            float near = MathF.Max(Distance * 0.01f, 0.01f), far = Distance * 100 + 10000;
            if (Ortho)
            {
                float h = 2 * Distance * MathF.Tan(Fov / 2);
                return Matrix4x4.CreateOrthographic(h * aspect, h, -far, far);
            }
            return Matrix4x4.CreatePerspectiveFieldOfView(Fov, aspect, near, far);
        }
    }

    public Matrix4x4 ViewProj => View * Projection;

    /// 화면 픽셀 크기의 월드 길이 (타깃 깊이 기준)
    public float PixelSize => 2 * Distance * MathF.Tan(Fov / 2) / MathF.Max(Height, 1);

    public Vector2 Project(Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), ViewProj);
        if (MathF.Abs(c.W) < 1e-9f) return new Vector2(float.NaN);
        return new Vector2((c.X / c.W * 0.5f + 0.5f) * Width, (1 - (c.Y / c.W * 0.5f + 0.5f)) * Height);
    }

    public float Depth(Vector3 p) => Vector3.Dot(p - Eye, -Dir);

    public (Vector3 origin, Vector3 dir) Ray(float x, float y)
    {
        Matrix4x4.Invert(ViewProj, out var inv);
        float nx = x / Width * 2 - 1, ny = 1 - y / Height * 2;
        var a = Vector4.Transform(new Vector4(nx, ny, -1, 1), inv);
        var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (pa, Vector3.Normalize(pb - pa));
    }

    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.008f;
        Pitch = Math.Clamp(Pitch + dy * 0.008f, -1.55f, 1.55f);
    }

    public void Pan(float dx, float dy)
    {
        var right = Vector3.Normalize(Vector3.Cross(Up, Dir));
        Target += (right * dx + Up * dy) * PixelSize;
    }

    /// 커서 위치를 향해 확대/축소
    public void Zoom(float factor, float x, float y)
    {
        var (o, d) = Ray(x, y);
        var before = PointAtTargetDepth(o, d);
        Distance = Math.Clamp(Distance * factor, 0.5f, 1e6f);
        var (o2, d2) = Ray(x, y);
        var after = PointAtTargetDepth(o2, d2);
        Target += before - after;
    }

    Vector3 PointAtTargetDepth(Vector3 o, Vector3 d)
    {
        // 타깃을 지나고 시선에 수직인 평면과의 교점
        var n = Dir;
        float t = Vector3.Dot(Target - o, n) / Vector3.Dot(d, n);
        return o + d * t;
    }

    public void AnimateTo(Vector3 target, float dist, float yaw, float pitch)
    {
        _t0 = Target; _d0 = Distance; _y0 = Yaw; _p0 = Pitch;
        _t1 = target; _d1 = dist; _p1 = pitch;
        // 가장 짧은 방향으로 회전
        float dy = (yaw - _y0) % (2 * MathF.PI);
        if (dy > MathF.PI) dy -= 2 * MathF.PI;
        if (dy < -MathF.PI) dy += 2 * MathF.PI;
        _y1 = _y0 + dy;
        _animStart = Environment.TickCount64;
    }

    /// 애니메이션 진행. 진행 중이면 true
    public bool Tick()
    {
        if (_animStart < 0) return false;
        double t = Math.Min(1, (Environment.TickCount64 - _animStart) / AnimMs);
        float e = (float)(1 - Math.Pow(1 - t, 3));
        Target = Vector3.Lerp(_t0, _t1, e);
        Distance = _d0 + (_d1 - _d0) * e;
        Yaw = _y0 + (_y1 - _y0) * e;
        Pitch = _p0 + (_p1 - _p0) * e;
        if (t >= 1) _animStart = -1;
        return true;
    }

    /// 주어진 방향(카메라 → 대상의 반대, 즉 눈의 방향)으로 보기
    public void LookFrom(Vector3 eyeDir, Vector3? target = null, float? dist = null)
    {
        eyeDir = Vector3.Normalize(eyeDir);
        float pitch = MathF.Asin(Math.Clamp(eyeDir.Z, -1, 1));
        if (MathF.Abs(pitch) > 1.55f) pitch = MathF.Sign(pitch) * 1.5699f;
        float yaw = MathF.Abs(eyeDir.X) + MathF.Abs(eyeDir.Y) < 1e-5f ? Yaw : MathF.Atan2(eyeDir.Y, eyeDir.X);
        AnimateTo(target ?? Target, dist ?? Distance, yaw, pitch);
    }

    public void Fit(Vector3 min, Vector3 max, bool animate = true)
    {
        var c = (min + max) / 2;
        float r = MathF.Max((max - min).Length() / 2, 5);
        float dist = r / MathF.Sin(Fov / 2) * 1.1f;
        if (animate) AnimateTo(c, dist, Yaw, Pitch);
        else { Target = c; Distance = dist; }
    }

    public void Home(Vector3 min, Vector3 max)
    {
        var c = (min + max) / 2;
        float r = MathF.Max((max - min).Length() / 2, 25);
        AnimateTo(c, r / MathF.Sin(Fov / 2) * 1.2f, -MathF.PI / 4, 0.6f);
    }
}
