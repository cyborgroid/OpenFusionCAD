using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFusionCAD.Kernel;
using OpenFusionCAD.Model;
using Plane = OpenFusionCAD.Model.Plane;
using OpenFusionCAD.Sketching;

namespace OpenFusionCAD.Viewport;

public record struct FaceRef(int Body, int Face);
public record struct EdgeRef(int Body, int Edge);

public sealed class SketchView
{
    public required SketchFeature Sketch;
    public List<ProfileInfo> Profiles = [];
    public bool Active;
    public bool Visible = true;
}

/// 뷰포트에 그릴 모든 것과 하이라이트 상태
public sealed class Scene
{
    public List<BodyMesh> Bodies = [];
    /// 바디별 모양 색 (RGBA, A<1 이면 반투명)
    public Dictionary<int, Vector4> BodyColors = [];
    public UI.MainWindow.VisualStyle Style = UI.MainWindow.VisualStyle.ShadedEdges;
    /// 캔버스 조작기(돌출 거리 화살표): 기준점, 방향, 길이
    public (Vector3 origin, Vector3 dir, float length, bool hot)? Manipulator;
    public HashSet<int> HiddenBodies = [];
    public List<SketchView> Sketches = [];

    public FaceRef? HoverFace;
    public HashSet<FaceRef> SelectedFaces = [];
    public EdgeRef? HoverEdge;
    public HashSet<EdgeRef> SelectedEdges = [];
    public HashSet<int> SelectedBodies = [];

    // 활성 스케치의 프로파일/엔티티 하이라이트
    public int? HoverProfile;
    public HashSet<int> SelectedProfiles = [];
    public int? HoverEntity;
    public HashSet<int> SelectedEntities = [];

    /// 명령 중 기준 모델 기준으로 계산한 모서리 하이라이트 (x1,y1,z1,x2,y2,z2 …)
    public float[] ExtraSelected = [];
    public float[] ExtraHover = [];

    /// 그리는 중인 미리보기 엔티티 (활성 스케치 평면 기준)
    public List<Entity> Preview = [];
    /// 스냅 표시 위치 (활성 스케치 2D)
    public Vector2? SnapMark;

    /// 스케치 평면 선택 모드: 원점 평면 표시
    public bool ShowOriginPlanes;
    public string? HoverPlane;
    public bool ShowOrigin = false; // Fusion: 원점 전구 기본 꺼짐
    public bool ShowGrid = true;
    public bool ShowSketchPoints = true;

    /// 바디가 바뀌면 증가 → GPU 버퍼 재업로드
    public int BodyVersion;

    public SketchView? ActiveSketch => Sketches.FirstOrDefault(s => s.Active);

    public (Vector3 min, Vector3 max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var b in Bodies.Where(b => !HiddenBodies.Contains(b.Index)))
        {
            min = Vector3.Min(min, new Vector3((float)b.BBox[0], (float)b.BBox[1], (float)b.BBox[2]));
            max = Vector3.Max(max, new Vector3((float)b.BBox[3], (float)b.BBox[4], (float)b.BBox[5]));
        }
        foreach (var s in Sketches.Where(s => s.Visible))
        {
            var f = new Frame(s.Sketch.Plane);
            foreach (var e in s.Sketch.Entities)
                foreach (var pl in SketchGeom.Polylines(e))
                    foreach (var p in pl)
                    {
                        var w = f.To3(p.X, p.Y);
                        min = Vector3.Min(min, w);
                        max = Vector3.Max(max, w);
                    }
        }
        if (min.X > max.X) return (new Vector3(-50), new Vector3(50));
        return (min, max);
    }

    public void ClearSelection()
    {
        SelectedFaces.Clear(); SelectedEdges.Clear(); SelectedBodies.Clear();
        SelectedProfiles.Clear(); SelectedEntities.Clear();
        HoverFace = null; HoverEdge = null; HoverProfile = null; HoverEntity = null;
    }
}
