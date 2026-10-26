using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFusionCAD.Model;

// 커널(kernel/src/model.rs)과 같은 JSON 형식. 이 JSON 이 곧 .ofcd 파일 형식이다.

public sealed class Plane
{
    public double[] Origin { get; set; } = [0, 0, 0];
    public double[] Normal { get; set; } = [0, 0, 1];
    public double[] Xdir { get; set; } = [1, 0, 0];

    public static Plane XY => new() { Origin = [0, 0, 0], Normal = [0, 0, 1], Xdir = [1, 0, 0] };
    public static Plane XZ => new() { Origin = [0, 0, 0], Normal = [0, -1, 0], Xdir = [1, 0, 0] };
    public static Plane YZ => new() { Origin = [0, 0, 0], Normal = [1, 0, 0], Xdir = [0, 1, 0] };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LineEntity), "line")]
[JsonDerivedType(typeof(RectEntity), "rect")]
[JsonDerivedType(typeof(CircleEntity), "circle")]
[JsonDerivedType(typeof(ArcEntity), "arc")]
[JsonDerivedType(typeof(ConstructionEntity), "construction")]
public abstract class Entity { }

public sealed class LineEntity : Entity { public double[] A { get; set; } = [0, 0]; public double[] B { get; set; } = [0, 0]; }
public sealed class RectEntity : Entity { public double[] A { get; set; } = [0, 0]; public double[] B { get; set; } = [0, 0]; }
public sealed class CircleEntity : Entity { public double[] C { get; set; } = [0, 0]; public double R { get; set; } }
public sealed class ArcEntity : Entity { public double[] A { get; set; } = [0, 0]; public double[] M { get; set; } = [0, 0]; public double[] B { get; set; } = [0, 0]; }
public sealed class ConstructionEntity : Entity { public double[] A { get; set; } = [0, 0]; public double[] B { get; set; } = [0, 0]; }

public sealed class Axis
{
    /// "x" | "y" | "line"
    public string Kind { get; set; } = "x";
    public int? Entity { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SketchFeature), "sketch")]
[JsonDerivedType(typeof(ExtrudeFeature), "extrude")]
[JsonDerivedType(typeof(RevolveFeature), "revolve")]
[JsonDerivedType(typeof(FilletFeature), "fillet")]
[JsonDerivedType(typeof(ChamferFeature), "chamfer")]
[JsonDerivedType(typeof(BoxFeature), "box")]
[JsonDerivedType(typeof(CylinderFeature), "cylinder")]
[JsonDerivedType(typeof(SphereFeature), "sphere")]
[JsonDerivedType(typeof(ImportFeature), "import")]
public abstract class Feature
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public bool Suppressed { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed class SketchFeature : Feature
{
    public Plane Plane { get; set; } = Plane.XY;
    public List<Entity> Entities { get; set; } = [];
    public override string Kind => "Sketch";
}

/// 바디를 만들거나 기존 바디와 결합하는 피처 공통 속성
public interface IBodyOp
{
    string Operation { get; set; }
    int? Target { get; set; }
}

public sealed class ExtrudeFeature : Feature, IBodyOp
{
    public string Sketch { get; set; } = "";
    public List<int>? Profiles { get; set; }
    public double Distance { get; set; } = 10;
    public double Distance2 { get; set; }
    /// "one" | "symmetric" | "two"
    public string Direction { get; set; } = "one";
    /// "new" | "join" | "cut" | "intersect"
    public string Operation { get; set; } = "new";
    public int? Target { get; set; }
    public override string Kind => "Extrude";
}

public sealed class RevolveFeature : Feature, IBodyOp
{
    public string Sketch { get; set; } = "";
    public List<int>? Profiles { get; set; }
    public Axis Axis { get; set; } = new();
    public double Angle { get; set; } = 360;
    public string Operation { get; set; } = "new";
    public int? Target { get; set; }
    public override string Kind => "Revolve";
}

public sealed class FilletFeature : Feature
{
    public int Body { get; set; }
    public List<int> Edges { get; set; } = [];
    public double Radius { get; set; } = 1;
    public override string Kind => "Fillet";
}

public sealed class ChamferFeature : Feature
{
    public int Body { get; set; }
    public List<int> Edges { get; set; } = [];
    public double Distance { get; set; } = 1;
    public override string Kind => "Chamfer";
}

public sealed class BoxFeature : Feature, IBodyOp
{
    public double[] Origin { get; set; } = [0, 0, 0];
    public double[] Size { get; set; } = [20, 20, 20];
    public string Operation { get; set; } = "new";
    public int? Target { get; set; }
    public override string Kind => "Box";
}

public sealed class CylinderFeature : Feature, IBodyOp
{
    public double[] Origin { get; set; } = [0, 0, 0];
    public double[] Axis { get; set; } = [0, 0, 1];
    public double Radius { get; set; } = 10;
    public double Height { get; set; } = 20;
    public string Operation { get; set; } = "new";
    public int? Target { get; set; }
    public override string Kind => "Cylinder";
}

public sealed class SphereFeature : Feature, IBodyOp
{
    public double[] Center { get; set; } = [0, 0, 0];
    public double Radius { get; set; } = 10;
    public string Operation { get; set; } = "new";
    public int? Target { get; set; }
    public override string Kind => "Sphere";
}

public sealed class ImportFeature : Feature, IBodyOp
{
    public string Path { get; set; } = "";
    public string Operation { get; set; } = "new";
    public int? Target { get; set; }
    public override string Kind => "Import";
}

public sealed class Document
{
    public int Version { get; set; } = 1;
    public List<Feature> Features { get; set; } = [];
    /// 타임라인 마커 (앞에서부터 계산할 피처 수, null = 전부)
    public int? Marker { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        WriteIndented = false,
    };

    public string ToJson(bool indented = false) =>
        JsonSerializer.Serialize(this, indented ? new JsonSerializerOptions(Json) { WriteIndented = true } : Json);

    public static Document FromJson(string json) => JsonSerializer.Deserialize<Document>(json, Json) ?? new Document();

    public Document Clone() => FromJson(ToJson());

    public int ActiveCount => Math.Min(Marker ?? Features.Count, Features.Count);

    /// 종류별 다음 이름 (Sketch1, Extrude2 …)
    public string NextName(string kind)
    {
        int n = Features.Count(f => f.Kind == kind) + 1;
        while (Features.Any(f => f.Name == $"{kind}{n}")) n++;
        return $"{kind}{n}";
    }

    public Feature? Find(string id) => Features.FirstOrDefault(f => f.Id == id);
}
