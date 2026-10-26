using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Sketching;
using Silk.NET.OpenGL;

namespace OpenFusionCAD.Viewport;

/// <summary>
/// OpenGL 3D 뷰포트. 배경 그라데이션, 그리드, 원점 축/평면, 바디(음영+모서리), 스케치,
/// 선택 하이라이트를 그린다. 4x MSAA 프레임버퍼에 그린 뒤 Avalonia 프레임버퍼로 복사한다.
/// </summary>
public sealed class ViewportControl : OpenGlControlBase
{
    public Scene Scene { get; set; } = new();
    public Camera Camera { get; } = new();
    public event Action? Rendered;

    GL? _gl;
    uint _litProg, _lineProg, _bgProg;
    int _uLitMvp, _uLitView, _uLitColor, _uLineMvp, _uLineColor;
    uint _bgVao, _bgVbo, _lineVao, _lineVbo, _triVao, _triVbo;
    uint _msFbo, _msColor, _msDepth; int _msW, _msH; bool _msaa = true;
    int _uploadedVersion = -1;
    readonly List<(uint vao, uint vbo, uint nbo, uint ebo, int count)> _bodyBuffers = [];

    // Fusion 풍 색상
    static readonly Vector4 BodyColor = new(0.72f, 0.74f, 0.77f, 1);
    static readonly Vector4 EdgeColor = new(0.08f, 0.08f, 0.09f, 1);
    static readonly Vector4 Accent = new(0.024f, 0.588f, 0.843f, 1);      // #0696D7
    static readonly Vector4 HoverAccent = new(0.35f, 0.75f, 0.95f, 1);
    static readonly Vector4 SketchColor = new(0.10f, 0.27f, 0.72f, 1);
    static readonly Vector4 ConstrColor = new(0.95f, 0.55f, 0.10f, 1);
    static readonly Vector4 PreviewColor = new(0.10f, 0.45f, 0.95f, 1);

    const string LitVs = @"
in vec3 aPos; in vec3 aNrm;
uniform mat4 uMvp; uniform mat4 uView;
out vec3 vN; out vec3 vP;
void main(){ gl_Position = uMvp*vec4(aPos,1.0); vN = mat3(uView)*aNrm; vP = (uView*vec4(aPos,1.0)).xyz; }";
    const string LitFs = @"
in vec3 vN; in vec3 vP; uniform vec4 uColor; out vec4 frag;
void main(){
  vec3 n = normalize(vN); vec3 v = normalize(-vP);
  if (dot(n, v) < 0.0) n = -n;
  vec3 l = normalize(vec3(0.35, 0.55, 1.0));
  float d = max(dot(n, l), 0.0); float h = max(dot(n, v), 0.0);
  float s = pow(max(dot(n, normalize(l + v)), 0.0), 48.0);
  vec3 c = uColor.rgb * (0.32 + 0.38*d + 0.38*h) + vec3(0.20) * s;
  frag = vec4(c, uColor.a);
}";
    const string LineVs = @"
in vec3 aPos; uniform mat4 uMvp;
void main(){ gl_Position = uMvp*vec4(aPos,1.0); }";
    const string LineFs = @"
uniform vec4 uColor; out vec4 frag;
void main(){ frag = uColor; }";
    const string BgVs = @"
in vec2 aPos; in vec3 aCol; out vec3 vC;
void main(){ gl_Position = vec4(aPos, 0.999, 1.0); vC = aCol; }";
    const string BgFs = @"
in vec3 vC; out vec4 frag;
void main(){ frag = vec4(vC, 1.0); }";

    string _header = "#version 330 core\n";

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        var ver = gl.ContextInfo.Version;
        _header = ver.Type == GlProfileType.OpenGLES ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        _litProg = Program(LitVs, LitFs);
        _lineProg = Program(LineVs, LineFs);
        _bgProg = Program(BgVs, BgFs);
        _uLitMvp = _gl.GetUniformLocation(_litProg, "uMvp");
        _uLitView = _gl.GetUniformLocation(_litProg, "uView");
        _uLitColor = _gl.GetUniformLocation(_litProg, "uColor");
        _uLineMvp = _gl.GetUniformLocation(_lineProg, "uMvp");
        _uLineColor = _gl.GetUniformLocation(_lineProg, "uColor");

        // 배경: 위 밝은 회색 → 아래 푸른 회색 (Fusion 기본 배경과 비슷한 톤)
        float[] bg = [-1, -1, 0.78f, 0.80f, 0.83f, 1, -1, 0.78f, 0.80f, 0.83f, 1, 1, 0.97f, 0.97f, 0.98f, -1, 1, 0.97f, 0.97f, 0.98f];
        _bgVao = _gl.GenVertexArray(); _bgVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_bgVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _bgVbo);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, bg, BufferUsageARB.StaticDraw);
        int stride = 5 * sizeof(float);
        uint aPos = (uint)_gl.GetAttribLocation(_bgProg, "aPos"), aCol = (uint)_gl.GetAttribLocation(_bgProg, "aCol");
        _gl.EnableVertexAttribArray(aPos);
        unsafe { _gl.VertexAttribPointer(aPos, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)0); }
        _gl.EnableVertexAttribArray(aCol);
        unsafe { _gl.VertexAttribPointer(aCol, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)(2 * sizeof(float))); }

        _lineVao = _gl.GenVertexArray(); _lineVbo = _gl.GenBuffer();
        _triVao = _gl.GenVertexArray(); _triVbo = _gl.GenBuffer();
        _uploadedVersion = -1;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl == null) return;
        FreeBodies();
        _gl.DeleteProgram(_litProg); _gl.DeleteProgram(_lineProg); _gl.DeleteProgram(_bgProg);
        DeleteMsaa();
        _gl = null;
    }

    uint Program(string vs, string fs)
    {
        var gl = _gl!;
        uint Compile(ShaderType t, string src)
        {
            uint s = gl.CreateShader(t);
            gl.ShaderSource(s, _header + src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new Exception($"{t}: {gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint p = gl.CreateProgram();
        uint v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
        gl.AttachShader(p, v); gl.AttachShader(p, f);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new Exception(gl.GetProgramInfoLog(p));
        gl.DeleteShader(v); gl.DeleteShader(f);
        return p;
    }

    void FreeBodies()
    {
        foreach (var (vao, vbo, nbo, ebo, _) in _bodyBuffers)
        {
            _gl!.DeleteVertexArray(vao); _gl.DeleteBuffer(vbo); _gl.DeleteBuffer(nbo); _gl.DeleteBuffer(ebo);
        }
        _bodyBuffers.Clear();
    }

    void UploadBodies()
    {
        var gl = _gl!;
        FreeBodies();
        uint aPos = (uint)gl.GetAttribLocation(_litProg, "aPos"), aNrm = (uint)gl.GetAttribLocation(_litProg, "aNrm");
        foreach (var b in Scene.Bodies)
        {
            uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer(), nbo = gl.GenBuffer(), ebo = gl.GenBuffer();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, b.Positions, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(aPos);
            unsafe { gl.VertexAttribPointer(aPos, 3, VertexAttribPointerType.Float, false, 0, (void*)0); }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, nbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, b.Normals, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(aNrm);
            unsafe { gl.VertexAttribPointer(aNrm, 3, VertexAttribPointerType.Float, false, 0, (void*)0); }
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, b.Indices, BufferUsageARB.StaticDraw);
            _bodyBuffers.Add((vao, vbo, nbo, ebo, b.Indices.Length));
        }
        gl.BindVertexArray(0);
        _uploadedVersion = Scene.BodyVersion;
    }

    void DeleteMsaa()
    {
        if (_gl == null || _msFbo == 0) return;
        _gl.DeleteFramebuffer(_msFbo); _gl.DeleteRenderbuffer(_msColor); _gl.DeleteRenderbuffer(_msDepth);
        _msFbo = 0;
    }

    void EnsureMsaa(int w, int h)
    {
        if (!_msaa || (_msFbo != 0 && w == _msW && h == _msH)) return;
        var gl = _gl!;
        DeleteMsaa();
        _msFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
        _msColor = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msColor);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba8, (uint)w, (uint)h);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msColor);
        _msDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msDepth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Depth24Stencil8, (uint)w, (uint)h);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _msDepth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            _msaa = false; // 다중 샘플링 미지원 → 직접 렌더링
            DeleteMsaa();
        }
        _msW = w; _msH = h;
    }

    protected override void OnOpenGlRender(GlInterface glIf, int fb)
    {
        var gl = _gl!;
        double scale = VisualRoot?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * scale)), h = Math.Max(1, (int)(Bounds.Height * scale));
        Camera.Width = (float)Bounds.Width; Camera.Height = (float)Bounds.Height;
        bool animating = Camera.Tick();

        EnsureMsaa(w, h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaa && _msFbo != 0 ? _msFbo : (uint)fb);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.ClearColor(0.9f, 0.9f, 0.92f, 1);
        gl.ClearDepth(1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // 배경
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(_bgProg);
        gl.BindVertexArray(_bgVao);
        gl.DrawArrays(PrimitiveType.TriangleFan, 0, 4);

        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        if (_uploadedVersion != Scene.BodyVersion) UploadBodies();
        var view = Camera.View;
        var vp = Camera.ViewProj;

        if (Scene.ShowGrid) { DrawGrid(vp); DrawGridAxes(vp); }
        if (Scene.ShowOrigin) { DrawAxes(vp); DrawOriginPlanes(vp, subtle: true); }

        // 바디 음영 (모서리가 위에 보이도록 polygon offset)
        gl.UseProgram(_litProg);
        SetMat(_uLitMvp, vp); SetMat(_uLitView, view);
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1, 1);
        for (int i = 0; i < Scene.Bodies.Count && i < _bodyBuffers.Count; i++)
        {
            var b = Scene.Bodies[i];
            if (Scene.HiddenBodies.Contains(b.Index)) continue;
            var (vao, _, _, _, count) = _bodyBuffers[i];
            gl.BindVertexArray(vao);
            if (Scene.Style == UI.MainWindow.VisualStyle.Wireframe) continue;
            var baseColor = Scene.BodyColors.TryGetValue(b.Index, out var bc) ? bc : BodyColor;
            var color = Scene.SelectedBodies.Contains(b.Index) ? Vector4.Lerp(baseColor, Accent, 0.55f) with { W = baseColor.W } : baseColor;
            if (color.W < 1) gl.DepthMask(false);
            SetColor(_uLitColor, color);
            unsafe { gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)0); }
            gl.DepthMask(true);
            // 면 하이라이트
            foreach (var fr in Scene.SelectedFaces.Where(f => f.Body == b.Index)) DrawFace(b, fr.Face, Vector4.Lerp(BodyColor, Accent, 0.75f));
            if (Scene.HoverFace is { } hf && hf.Body == b.Index && !Scene.SelectedFaces.Contains(hf)) DrawFace(b, hf.Face, Vector4.Lerp(BodyColor, HoverAccent, 0.55f));
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // 모서리
        foreach (var b in Scene.Bodies)
        {
            if (Scene.HiddenBodies.Contains(b.Index)) continue;
            if (Scene.Style != UI.MainWindow.VisualStyle.Shaded) DrawLines(b.Edges, vp, Scene.Style == UI.MainWindow.VisualStyle.Wireframe ? new Vector4(0.15f, 0.2f, 0.3f, 1) : EdgeColor);
            var sel = new List<float>(); var hov = new List<float>();
            for (int k = 0; k < b.EdgeIds.Length; k++)
            {
                var er = new EdgeRef(b.Index, (int)b.EdgeIds[k]);
                var target = Scene.SelectedEdges.Contains(er) ? sel : Scene.HoverEdge == er ? hov : null;
                if (target != null) for (int j = 0; j < 6; j++) target.Add(b.Edges[k * 6 + j]);
            }
            if (sel.Count > 0) DrawThick(sel.ToArray(), vp, Accent);
            if (hov.Count > 0) DrawThick(hov.ToArray(), vp, HoverAccent);
        }

        if (Scene.Manipulator is { } mp) DrawManipulator(vp, mp.origin, mp.dir, mp.length, mp.hot);
        if (Scene.ExtraSelected.Length > 0) DrawThick(Scene.ExtraSelected, vp, Accent);
        if (Scene.ExtraHover.Length > 0) DrawThick(Scene.ExtraHover, vp, HoverAccent);

        DrawSketches(vp);
        if (Scene.ShowOriginPlanes) DrawOriginPlanes(vp);

        if (_msaa && _msFbo != 0)
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)fb);
            gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        }
        gl.BindVertexArray(0);
        gl.UseProgram(0);

        Rendered?.Invoke();
        if (animating) Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    void DrawFace(Kernel.BodyMesh b, int face, Vector4 color)
    {
        if (face < 0 || face + 1 >= b.FaceStart.Length) return;
        uint start = b.FaceStart[face], end = b.FaceStart[face + 1];
        SetColor(_uLitColor, color);
        gl_DrawRange(start, end);
    }

    unsafe void gl_DrawRange(uint startTri, uint endTri)
    {
        _gl!.DrawElements(PrimitiveType.Triangles, (endTri - startTri) * 3, DrawElementsType.UnsignedInt, (void*)(startTri * 3 * sizeof(uint)));
    }

    unsafe void SetMat(int loc, Matrix4x4 m)
    {
        _gl!.UniformMatrix4(loc, 1, false, (float*)&m);
    }

    void SetColor(int loc, Vector4 c) => _gl!.Uniform4(loc, c.X, c.Y, c.Z, c.W);

    /// 동적 선분 그리기 (x1,y1,z1,x2,y2,z2 …)
    void DrawLines(float[] segs, Matrix4x4 vp, Vector4 color, bool noDepth = false)
    {
        if (segs.Length < 6) return;
        var gl = _gl!;
        gl.UseProgram(_lineProg);
        SetMat(_uLineMvp, vp);
        SetColor(_uLineColor, color);
        gl.BindVertexArray(_lineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, segs, BufferUsageARB.StreamDraw);
        uint aPos = (uint)gl.GetAttribLocation(_lineProg, "aPos");
        gl.EnableVertexAttribArray(aPos);
        unsafe { gl.VertexAttribPointer(aPos, 3, VertexAttribPointerType.Float, false, 0, (void*)0); }
        if (noDepth) gl.Disable(EnableCap.DepthTest);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(segs.Length / 3));
        if (noDepth) gl.Enable(EnableCap.DepthTest);
    }

    /// 굵은 선: 화면 공간에서 약간 어긋난 선을 여러 번 그린다 (core 프로파일은 lineWidth>1 미지원)
    void DrawThick(float[] segs, Matrix4x4 vp, Vector4 color, float px = 1.2f)
    {
        var off = Camera.PixelSize * px;
        var right = Vector3.Normalize(Vector3.Cross(Camera.Dir, Vector3.UnitZ) is var r && r.LengthSquared() > 1e-6f ? r : Vector3.UnitX);
        var up = Vector3.Normalize(Vector3.Cross(right, Camera.Dir));
        foreach (var d in new[] { Vector3.Zero, right * off, -right * off, up * off, -up * off })
        {
            var s = (float[])segs.Clone();
            for (int i = 0; i < s.Length; i += 3) { s[i] += d.X; s[i + 1] += d.Y; s[i + 2] += d.Z; }
            DrawLines(s, vp, color, noDepth: true);
        }
    }

    void DrawTris(float[] tris, Matrix4x4 vp, Vector4 color)
    {
        if (tris.Length < 9) return;
        var gl = _gl!;
        gl.UseProgram(_lineProg);
        SetMat(_uLineMvp, vp);
        SetColor(_uLineColor, color);
        gl.BindVertexArray(_triVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _triVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, tris, BufferUsageARB.StreamDraw);
        uint aPos = (uint)gl.GetAttribLocation(_lineProg, "aPos");
        gl.EnableVertexAttribArray(aPos);
        unsafe { gl.VertexAttribPointer(aPos, 3, VertexAttribPointerType.Float, false, 0, (void*)0); }
        gl.DepthMask(false);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(tris.Length / 3));
        gl.DepthMask(true);
    }

    float GridStep()
    {
        // 화면 약 40px 마다 한 칸이 되도록 1-2-5 단위로 조절
        float target = Camera.PixelSize * 40;
        float p = MathF.Pow(10, MathF.Floor(MathF.Log10(target)));
        foreach (var m in new[] { 1f, 2f, 5f, 10f }) if (p * m >= target) return p * m;
        return p * 10;
    }

    void DrawGrid(Matrix4x4 vp)
    {
        var active = Scene.ActiveSketch;
        var frame = active != null ? new Frame(active.Sketch.Plane) : new Frame(Plane.XY);
        float step = GridStep();
        int n = 40;
        var c2 = frame.To2(Camera.Target);
        float cx = MathF.Round(c2.X / step) * step, cy = MathF.Round(c2.Y / step) * step;
        var minor = new List<float>(); var major = new List<float>();
        for (int i = -n; i <= n; i++)
        {
            var list = (Math.Abs(Math.Round((cx + i * step) / step)) % 5 == 0) ? major : minor;
            Add(list, frame.To3(cx + i * step, cy - n * step), frame.To3(cx + i * step, cy + n * step));
            var list2 = (Math.Abs(Math.Round((cy + i * step) / step)) % 5 == 0) ? major : minor;
            Add(list2, frame.To3(cx - n * step, cy + i * step), frame.To3(cx + n * step, cy + i * step));
        }
        DrawLines(minor.ToArray(), vp, new Vector4(0.45f, 0.48f, 0.53f, 0.18f));
        DrawLines(major.ToArray(), vp, new Vector4(0.40f, 0.43f, 0.48f, 0.38f));
    }

    static void Add(List<float> l, Vector3 a, Vector3 b) { l.Add(a.X); l.Add(a.Y); l.Add(a.Z); l.Add(b.X); l.Add(b.Y); l.Add(b.Z); }

    void DrawAxes(Matrix4x4 vp)
    {
        float len = GridStep() * 5;
        DrawLines([0, 0, 0, len, 0, 0], vp, new Vector4(0.85f, 0.15f, 0.15f, 1), noDepth: false);
        DrawLines([0, 0, 0, 0, len, 0], vp, new Vector4(0.20f, 0.65f, 0.20f, 1));
        DrawLines([0, 0, 0, 0, 0, len], vp, new Vector4(0.15f, 0.35f, 0.90f, 1));
    }

    /// Fusion 처럼 그리드 위에 X(빨강)·Y(초록) 축선을 표시
    void DrawGridAxes(Matrix4x4 vp)
    {
        float len = GridStep() * 40;
        DrawLines([-len, 0, 0, len, 0, 0], vp, new Vector4(0.85f, 0.25f, 0.25f, 0.55f));
        DrawLines([0, -len, 0, 0, len, 0], vp, new Vector4(0.25f, 0.65f, 0.25f, 0.55f));
    }

    void DrawOriginPlanes(Matrix4x4 vp, bool subtle = false)
    {
        float size = Picking.OriginPlaneSize * Camera.Distance / 200;
        foreach (var (name, plane) in new[] { ("XY", Plane.XY), ("XZ", Plane.XZ), ("YZ", Plane.YZ) })
        {
            var f = new Frame(plane);
            var a = f.To3(0, 0); var b = f.To3(size, 0); var c = f.To3(size, size); var d = f.To3(0, size);
            bool hover = Scene.HoverPlane == name;
            var fill = hover ? new Vector4(1.0f, 0.62f, 0.15f, 0.55f) : new Vector4(1.0f, 0.70f, 0.30f, subtle ? 0.10f : 0.28f);
            DrawTris([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z, a.X, a.Y, a.Z, c.X, c.Y, c.Z, d.X, d.Y, d.Z], vp, fill);
            var edge = new List<float>(); Add(edge, a, b); Add(edge, b, c); Add(edge, c, d); Add(edge, d, a);
            DrawLines(edge.ToArray(), vp, new Vector4(0.85f, 0.45f, 0.05f, hover ? 1 : 0.7f));
        }
    }

    void DrawSketches(Matrix4x4 vp)
    {
        foreach (var sv in Scene.Sketches)
        {
            if (!sv.Visible && !sv.Active) continue;
            var f = new Frame(sv.Sketch.Plane);
            // 프로파일 채우기 (활성 스케치 또는 선택 중인 프로파일)
            for (int i = 0; i < sv.Profiles.Count; i++)
            {
                bool sel = sv.Active ? Scene.SelectedProfiles.Contains(i) : false;
                bool hov = sv.Active && Scene.HoverProfile == i;
                Vector4 col = sel ? new Vector4(0.30f, 0.62f, 0.95f, 0.45f) : hov ? new Vector4(1.0f, 0.80f, 0.45f, 0.50f) : new Vector4(1.0f, 0.86f, 0.62f, sv.Active ? 0.22f : 0.12f);
                DrawTris(sv.Profiles[i].Tris, vp, col);
            }
            var normal = new List<float>(); var constr = new List<float>(); var hover = new List<float>(); var selected = new List<float>();
            for (int i = 0; i < sv.Sketch.Entities.Count; i++)
            {
                var e = sv.Sketch.Entities[i];
                var target = sv.Active && Scene.SelectedEntities.Contains(i) ? selected : sv.Active && Scene.HoverEntity == i ? hover : e is ConstructionEntity ? constr : normal;
                foreach (var pl in SketchGeom.Polylines(e))
                {
                    bool dashed = e is ConstructionEntity;
                    for (int k = 0; k + 1 < pl.Count; k++)
                    {
                        if (dashed) AddDashed(target, f, pl[k], pl[k + 1]);
                        else Add(target, f.To3(pl[k].X, pl[k].Y), f.To3(pl[k + 1].X, pl[k + 1].Y));
                    }
                }
            }
            var alpha = sv.Active ? 1f : 0.75f;
            DrawLines(normal.ToArray(), vp, SketchColor with { W = alpha }, noDepth: sv.Active);
            DrawLines(constr.ToArray(), vp, ConstrColor with { W = alpha }, noDepth: sv.Active);
            if (hover.Count > 0) DrawThick(hover.ToArray(), vp, HoverAccent);
            if (selected.Count > 0) DrawThick(selected.ToArray(), vp, Accent);
            if (sv.Active)
            {
                // 끝점 표시 (작은 사각형)
                var pts = new List<float>();
                float s = Camera.PixelSize * 2.5f;
                if (Scene.ShowSketchPoints)
                    foreach (var e in sv.Sketch.Entities)
                        foreach (var p in SketchGeom.SnapPoints(e)) Square(pts, f, p, s);
                DrawLines(pts.ToArray(), vp, SketchColor, noDepth: true);
                // 그리는 중 미리보기
                var prev = new List<float>();
                foreach (var e in Scene.Preview)
                    foreach (var pl in SketchGeom.Polylines(e))
                        for (int k = 0; k + 1 < pl.Count; k++) Add(prev, f.To3(pl[k].X, pl[k].Y), f.To3(pl[k + 1].X, pl[k + 1].Y));
                DrawLines(prev.ToArray(), vp, PreviewColor, noDepth: true);
                if (Scene.SnapMark is { } sm)
                {
                    var sq = new List<float>();
                    Square(sq, f, sm, Camera.PixelSize * 5);
                    DrawThick(sq.ToArray(), vp, new Vector4(0.0f, 0.65f, 0.25f, 1), 0.6f);
                }
            }
        }
    }

    /// 돌출 거리 조작기: 기준점에서 방향으로 화살표 (Fusion 의 파란 화살표)
    void DrawManipulator(Matrix4x4 vp, Vector3 o, Vector3 dir, float length, bool hot)
    {
        var tip = o + dir * length;
        var color = hot ? new Vector4(0.35f, 0.75f, 0.95f, 1) : Accent;
        DrawThick([o.X, o.Y, o.Z, tip.X, tip.Y, tip.Z], vp, color, 1.0f);
        // 원뿔 화살촉
        float h = Camera.PixelSize * 22, r = Camera.PixelSize * 7;
        var side = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var up = Vector3.Cross(dir, side);
        var tris = new List<float>();
        var apex = tip + dir * h * MathF.Sign(length == 0 ? 1 : length);
        for (int i = 0; i < 16; i++)
        {
            float a0 = i * MathF.Tau / 16, a1 = (i + 1) * MathF.Tau / 16;
            var p0 = tip + (side * MathF.Cos(a0) + up * MathF.Sin(a0)) * r;
            var p1 = tip + (side * MathF.Cos(a1) + up * MathF.Sin(a1)) * r;
            tris.AddRange([apex.X, apex.Y, apex.Z, p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z]);
        }
        _gl!.Disable(EnableCap.DepthTest);
        DrawTris(tris.ToArray(), vp, color);
        _gl.Enable(EnableCap.DepthTest);
    }

    static void Square(List<float> l, Frame f, Vector2 p, float s)
    {
        var a = f.To3(p.X - s, p.Y - s); var b = f.To3(p.X + s, p.Y - s); var c = f.To3(p.X + s, p.Y + s); var d = f.To3(p.X - s, p.Y + s);
        Add(l, a, b); Add(l, b, c); Add(l, c, d); Add(l, d, a);
    }

    void AddDashed(List<float> l, Frame f, Vector2 a, Vector2 b)
    {
        float dash = Camera.PixelSize * 8;
        float len = Vector2.Distance(a, b);
        if (len < 1e-6f) return;
        var dir = (b - a) / len;
        for (float t = 0; t < len; t += dash * 2)
        {
            var p = a + dir * t; var q = a + dir * MathF.Min(t + dash, len);
            Add(l, f.To3(p.X, p.Y), f.To3(q.X, q.Y));
        }
    }

    public void Invalidate() => RequestNextFrameRendering();
}
