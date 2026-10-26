using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace OpenFusionCAD.UI;

/// <summary>
/// 직접 그린 24×24 벡터 아이콘. Autodesk 아이콘/로고는 사용하지 않는다.
/// 각 아이콘은 (경로, 채움색, 선색) 레이어 목록이다.
/// </summary>
public static class Icons
{
    const string Blue = "#2F7FD8", LightBlue = "#BBD8F5", Orange = "#F09A2B", LightOrange = "#FBD9A6", Gray = "#6B7280",
        LightGray = "#D9DDE3", Green = "#2E9E5B", Red = "#D64545", Dark = "#3A3F47", Yellow = "#F2C744";

    static readonly Dictionary<string, (string path, string? fill, string? stroke)[]> Defs = new()
    {
        ["sketch"] = [("M3,15 L13,15 L13,21 L3,21 Z", LightBlue, Blue), ("M12,13 L19,4 L21,6 L14,15 L11,16 Z", Yellow, Dark)],
        ["finish"] = [("M4,12 L10,18 L20,6", null, Green)],
        ["extrude"] = [("M4,16 L12,20 L20,16 L12,12 Z", LightOrange, Orange), ("M4,16 L4,10 L12,14 L12,20 Z M12,14 L20,10 L20,16 L12,20 Z", "#E8B66E", Orange), ("M12,9 L12,2 M9,5 L12,2 L15,5", null, Blue)],
        ["revolve"] = [("M12,3 L12,21", null, Gray), ("M6,7 A6,3 0 1 0 18,7", null, Blue), ("M16,4 L18,7 L15,8", null, Blue), ("M8,10 L16,10 L16,18 L8,18 Z", LightOrange, Orange)],
        ["fillet"] = [("M4,20 L4,4 L10,4 Q20,4 20,14 L20,20 Z", LightOrange, Orange), ("M10,4 Q20,4 20,14", null, Blue)],
        ["chamfer"] = [("M4,20 L4,4 L12,4 L20,12 L20,20 Z", LightOrange, Orange), ("M12,4 L20,12", null, Blue)],
        ["box"] = [("M4,8 L12,4 L20,8 L12,12 Z", "#F6C886", Orange), ("M4,8 L12,12 L12,21 L4,17 Z", LightOrange, Orange), ("M12,12 L20,8 L20,17 L12,21 Z", "#E8B66E", Orange)],
        ["cylinder"] = [("M5,6 L5,18 A7,3 0 0 0 19,18 L19,6", LightOrange, Orange), ("M5,6 A7,3 0 1 0 19,6 A7,3 0 1 0 5,6 Z", "#F6C886", Orange)],
        ["sphere"] = [("M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z", LightOrange, Orange), ("M3,12 A9,3 0 0 0 21,12", null, Orange)],
        ["line"] = [("M4,19 L20,5", null, Blue), ("M2.5,17.5 h3 v3 h-3 Z M18.5,3.5 h3 v3 h-3 Z", "#FFFFFF", Dark)],
        ["rect"] = [("M4,6 L20,6 L20,18 L4,18 Z", LightBlue, Blue)],
        ["circle"] = [("M4,12 A8,8 0 1 0 20,12 A8,8 0 1 0 4,12 Z", LightBlue, Blue), ("M11,11 h2 v2 h-2 Z", Dark, Dark)],
        ["arc"] = [("M4,18 A9,9 0 0 1 20,18", null, Blue), ("M2.5,16.5 h3 v3 h-3 Z M18.5,16.5 h3 v3 h-3 Z M10.5,7.5 h3 v3 h-3 Z", "#FFFFFF", Dark)],
        ["construction"] = [("M4,19 L7,16 M9,14 L12,11 M14,9 L17,6 M19,4 L20,3", null, Orange)],
        ["trim"] = [("M6,4 L18,20 M18,4 L6,20", null, Red)],
        ["insert"] = [("M5,3 L15,3 L19,7 L19,21 L5,21 Z", "#FFFFFF", Gray), ("M12,8 L12,17 M8,13 L12,17 L16,13", null, Blue)],
        ["export"] = [("M5,3 L15,3 L19,7 L19,21 L5,21 Z", "#FFFFFF", Gray), ("M12,17 L12,8 M8,12 L12,8 L16,12", null, Green)],
        ["measure"] = [("M3,16 L16,3 L21,8 L8,21 Z", Yellow, Dark), ("M7,12 L9,14 M10,9 L12,11 M13,6 L15,8", null, Dark)],
        ["select"] = [("M6,3 L6,19 L10,15 L13,21 L15,20 L12,14 L18,14 Z", "#FFFFFF", Dark)],
        ["plane"] = [("M3,9 L14,4 L21,9 L10,15 Z", LightOrange, Orange)],
        ["undo"] = [("M9,6 L4,11 L9,16 M4,11 L14,11 Q20,11 20,17", null, Dark)],
        ["redo"] = [("M15,6 L20,11 L15,16 M20,11 L10,11 Q4,11 4,17", null, Dark)],
        ["save"] = [("M4,4 L17,4 L20,7 L20,20 L4,20 Z", LightGray, Dark), ("M7,4 L7,9 L16,9 L16,4 M7,20 L7,14 L17,14 L17,20", null, Dark)],
        ["file"] = [("M5,3 L15,3 L19,7 L19,21 L5,21 Z", "#FFFFFF", Dark), ("M15,3 L15,7 L19,7", null, Dark)],
        ["grid"] = [("M3,3 h7 v7 h-7 Z M14,3 h7 v7 h-7 Z M3,14 h7 v7 h-7 Z M14,14 h7 v7 h-7 Z", LightGray, Dark)],
        ["user"] = [("M8,8 A4,4 0 1 0 16,8 A4,4 0 1 0 8,8 Z", LightGray, Dark), ("M4,21 Q4,14 12,14 Q20,14 20,21", null, Dark)],
        ["help"] = [("M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z", null, Dark), ("M9.5,9.5 Q9.5,7 12,7 Q14.5,7 14.5,9.5 Q14.5,11 12,12 L12,14 M12,16.5 L12,17.5", null, Dark)],
        ["orbit"] = [("M4,12 A8,4 0 1 0 20,12 A8,4 0 1 0 4,12", null, Dark), ("M12,4 L12,20", null, Dark), ("M17,15 L20,12 L22,15", null, Dark)],
        ["pan"] = [("M12,3 L12,21 M3,12 L21,12 M9,6 L12,3 L15,6 M9,18 L12,21 L15,18 M6,9 L3,12 L6,15 M18,9 L21,12 L18,15", null, Dark)],
        ["zoom"] = [("M4,10 A6,6 0 1 0 16,10 A6,6 0 1 0 4,10 Z", null, Dark), ("M14.5,14.5 L21,21 M7,10 L13,10 M10,7 L10,13", null, Dark)],
        ["fit"] = [("M3,8 L3,3 L8,3 M16,3 L21,3 L21,8 M21,16 L21,21 L16,21 M8,21 L3,21 L3,16", null, Dark), ("M8,8 h8 v8 h-8 Z", LightGray, Dark)],
        ["display"] = [("M3,5 h18 v12 h-18 Z", LightGray, Dark), ("M9,21 h6 M12,17 v4", null, Dark)],
        ["home"] = [("M3,11 L12,3 L21,11 M6,9 L6,21 L18,21 L18,9", null, Dark), ("M10,21 L10,15 L14,15 L14,21", null, Dark)],
        ["eye"] = [("M2,12 Q12,3 22,12 Q12,21 2,12 Z", "#FFFFFF", Dark), ("M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z", Dark, Dark)],
        ["eyeoff"] = [("M2,12 Q12,3 22,12 Q12,21 2,12 Z", "#FFFFFF", Gray), ("M4,20 L20,4", null, Gray)],
        ["body"] = [("M4,8 L12,4 L20,8 L20,17 L12,21 L4,17 Z", LightOrange, Orange)],
        ["folder"] = [("M3,6 L9,6 L11,8 L21,8 L21,19 L3,19 Z", Yellow, "#B58A12")],
        ["origin"] = [("M12,12 L21,12", null, Red), ("M12,12 L12,3", null, Green), ("M12,12 L5,19", null, Blue)],
        ["settings"] = [("M12,8 A4,4 0 1 0 12.01,8 Z", null, Dark), ("M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M19,5 L17,7 M7,17 L5,19", null, Dark)],
        ["first"] = [("M6,5 L6,19 M18,5 L9,12 L18,19 Z", Dark, Dark)],
        ["prev"] = [("M17,5 L8,12 L17,19 Z", Dark, Dark)],
        ["play"] = [("M7,5 L16,12 L7,19 Z", Dark, Dark)],
        ["last"] = [("M18,5 L18,19 M6,5 L15,12 L6,19 Z", Dark, Dark)],
        ["cloud"] = [("M7,18 L18,18 A4,4 0 0 0 17,10 A6,6 0 0 0 6,11 A3.5,3.5 0 0 0 7,18 Z", LightBlue, Blue)],
        ["error"] = [("M12,3 L22,20 L2,20 Z", "#FDE2E2", Red), ("M12,9 L12,14 M12,16.5 L12,17.5", null, Red)],
        ["component"] = [("M4,8 L12,4 L20,8 L20,17 L12,21 L4,17 Z", "#FFFFFF", Gray), ("M15,15 h6 M18,12 v6", null, Green)],
        ["form"] = [("M4,14 Q4,5 12,5 Q20,5 20,14 Q20,20 12,20 Q4,20 4,14 Z", "#E3D2F5", "#7B4BC4"), ("M8,11 L16,11 M8,15 L16,15", null, "#7B4BC4")],
        ["presspull"] = [("M4,12 L12,16 L20,12 L12,8 Z", LightOrange, Orange), ("M12,7 L12,1 M9,4 L12,1 L15,4 M12,17 L12,23 M9,20 L12,23 L15,20", null, Blue)],
        ["hole"] = [("M3,9 L12,5 L21,9 L12,13 Z", LightOrange, Orange), ("M3,9 L3,15 L12,19 L21,15 L21,9", "#E8B66E", Orange), ("M9,9 A3,1.5 0 1 0 15,9 A3,1.5 0 1 0 9,9 Z", Dark, Dark)],
        ["shell"] = [("M4,8 L12,4 L20,8 L20,17 L12,21 L4,17 Z", LightOrange, Orange), ("M7,9 L12,6.5 L17,9 L17,15 L12,18 L7,15 Z", "#FFFFFF", Orange)],
        ["move"] = [("M12,2 L12,22 M2,12 L22,12 M9,5 L12,2 L15,5 M9,19 L12,22 L15,19 M5,9 L2,12 L5,15 M19,9 L22,12 L19,15", null, Blue), ("M9,9 h6 v6 h-6 Z", LightOrange, Orange)],
        ["appearance"] = [("M4,20 Q2,12 8,6 Q14,1 19,5 Q22,8 18,11 Q15,13 18,16 Q20,20 14,21 Q8,22 4,20 Z", "#F5E6C8", Dark), ("M8,9 h2 v2 h-2 Z", Red, Red), ("M12,7 h2 v2 h-2 Z", Blue, Blue), ("M8,14 h2 v2 h-2 Z", Green, Green)],
        ["combine"] = [("M3,6 h11 v11 h-11 Z", LightOrange, Orange), ("M10,10 h11 v11 h-11 Z", "#E8B66E", Orange)],
        ["pattern"] = [("M3,3 h6 v6 h-6 Z M15,3 h6 v6 h-6 Z M3,15 h6 v6 h-6 Z M15,15 h6 v6 h-6 Z", LightOrange, Orange)],
        ["joint"] = [("M5,12 A4,4 0 1 0 13,12 A4,4 0 1 0 5,12 Z", LightGray, Dark), ("M11,12 A4,4 0 1 0 19,12 A4,4 0 1 0 11,12 Z", "#FFFFFF", Dark)],
        ["offsetplane"] = [("M3,15 L13,10 L21,15 L11,20 Z", LightOrange, Orange), ("M3,9 L13,4 L21,9 L11,14 Z", "#FFFFFF", Orange)],
        ["axis"] = [("M3,21 L21,3", null, Orange), ("M8,12 A6,3 0 1 0 16,12", null, Gray)],
        ["interference"] = [("M3,6 h11 v11 h-11 Z", LightBlue, Blue), ("M10,10 h11 v11 h-11 Z", LightOrange, Orange), ("M10,10 h4 v7 h-4 Z", Red, Red)],
        ["section"] = [("M4,8 L12,4 L20,8 L20,17 L12,21 L4,17 Z", LightOrange, Orange), ("M2,13 L22,13", null, Red)],
        ["canvas"] = [("M3,5 h18 v14 h-18 Z", "#FFFFFF", Dark), ("M5,16 L10,10 L14,14 L17,11 L20,16 Z", LightBlue, Blue)],
        ["decal"] = [("M5,4 h14 v16 h-14 Z", "#FFFFFF", Dark), ("M8,8 h8 v6 h-8 Z", Yellow, Orange)],
        ["window"] = [("M3,5 h18 v14 h-18 Z", null, Blue), ("M7,9 h6 v6 h-6 Z", LightBlue, Blue)],
        ["polygon"] = [("M12,3 L20,8 L20,16 L12,21 L4,16 L4,8 Z", LightBlue, Blue)],
        ["rectcenter"] = [("M4,6 L20,6 L20,18 L4,18 Z", LightBlue, Blue), ("M11,11 h2 v2 h-2 Z", Dark, Dark)],
        ["point"] = [("M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z", Blue, Blue)],
        ["dimension"] = [("M4,6 L4,18 M20,6 L20,18 M4,12 L20,12 M7,9 L4,12 L7,15 M17,9 L20,12 L17,15", null, Dark)],
        ["sketchfillet"] = [("M4,20 L4,10 Q4,4 10,4 L20,4", null, Blue)],
        ["offset"] = [("M4,18 L4,8 L16,8", null, Blue), ("M8,20 L8,12 L20,12", null, Orange)],
        ["mirror"] = [("M12,2 L12,22", null, Gray), ("M10,6 L3,12 L10,18 Z", LightBlue, Blue), ("M14,6 L21,12 L14,18 Z", "#FFFFFF", Blue)],
        ["circpattern"] = [("M10,3 h4 v4 h-4 Z M17,10 h4 v4 h-4 Z M10,17 h4 v4 h-4 Z M3,10 h4 v4 h-4 Z", LightBlue, Blue)],
        ["project"] = [("M4,16 L12,20 L20,16 L12,12 Z", LightOrange, Orange), ("M12,3 L12,11 M9,8 L12,11 L15,8", null, Blue)],
        ["c-horizontal"] = [("M3,12 L21,12", null, Dark)],
        ["c-coincident"] = [("M5,19 L19,5", null, Dark), ("M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z", "#FFFFFF", Dark)],
        ["c-tangent"] = [("M4,16 A8,8 0 0 1 20,16", null, Dark), ("M2,8 L22,8", null, Dark)],
        ["c-equal"] = [("M5,9 L19,9 M5,15 L19,15", null, Dark)],
        ["c-parallel"] = [("M8,3 L4,21 M20,3 L16,21", null, Dark)],
        ["c-perpendicular"] = [("M4,20 L20,20 M8,20 L8,4", null, Dark)],
        ["c-fix"] = [("M8,10 L16,10 L16,20 L8,20 Z", Yellow, Dark), ("M9,10 L9,7 A3,3 0 0 1 15,7 L15,10", null, Dark)],
        ["c-midpoint"] = [("M3,18 L21,6", null, Dark), ("M10,10 L14,10 L12,14 Z", Dark, Dark)],
        ["c-concentric"] = [("M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12 Z", null, Dark)],
        ["c-collinear"] = [("M2,12 L9,12 M15,12 L22,12", null, Dark)],
        ["bulb"] = [("M8,13 A5,5 0 1 1 16,13 L15,16 L9,16 Z", Yellow, "#8A6A00"), ("M9,18 L15,18 M10,20 L14,20", null, "#8A6A00")],
        ["bulboff"] = [("M8,13 A5,5 0 1 1 16,13 L15,16 L9,16 Z", "#FFFFFF", Gray), ("M9,18 L15,18 M10,20 L14,20", null, Gray)],
        ["extensions"] = [("M5,9 h4 a2,2 0 1 1 4,0 h4 v4 a2,2 0 1 1 0,4 v4 h-12 Z", LightGray, Dark)],
        ["jobstatus"] = [("M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z", null, Dark), ("M12,6 L12,12 L16,14", null, Dark)],
        ["bell"] = [("M6,17 L6,11 A6,6 0 0 1 18,11 L18,17 L20,19 L4,19 Z", LightGray, Dark), ("M10,21 h4", null, Dark)],
        ["lookat"] = [("M3,5 h14 v10 h-14 Z", LightGray, Dark), ("M20,10 L20,20 L10,20", null, Blue)],
        ["gridsnap"] = [("M3,3 h18 v18 h-18 Z M9,3 v18 M15,3 v18 M3,9 h18 M3,15 h18", null, Dark)],
        ["viewports"] = [("M3,4 h18 v16 h-18 Z M12,4 v16 M3,12 h18", null, Dark)],
        ["info"] = [("M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z", "#FFFFFF", Gray), ("M12,10 L12,17 M12,6.5 L12,7.5", null, Gray)],
        ["comment"] = [("M4,5 h16 v10 h-9 l-4,4 v-4 h-3 Z", "#FFFFFF", Dark)],
        ["generic"] = [("M5,5 h14 v14 h-14 Z", "#F1F3F5", "#9AA0A8"), ("M9,12 h6", null, "#9AA0A8")],
    };

    public static Control Make(string name, double size = 24)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (Defs.TryGetValue(name, out var layers) || Defs.TryGetValue("generic", out layers))
            foreach (var (path, fill, stroke) in layers)
                canvas.Children.Add(new Path
                {
                    Data = Geometry.Parse(path),
                    Fill = fill == null ? null : Brush.Parse(fill),
                    Stroke = stroke == null ? null : Brush.Parse(stroke),
                    StrokeThickness = 1.4,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                });
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    public static string ForFeature(string kind) => kind switch
    {
        "Sketch" => "sketch",
        "Extrude" => "extrude",
        "Revolve" => "revolve",
        "Fillet" => "fillet",
        "Chamfer" => "chamfer",
        "Box" => "box",
        "Cylinder" => "cylinder",
        "Sphere" => "sphere",
        "Import" => "insert",
        "PressPull" => "presspull",
        "Hole" => "hole",
        "Shell" => "shell",
        "Move" => "move",
        "Remove" => "trim",
        _ => "body",
    };
}
