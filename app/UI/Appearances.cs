using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenFusionCAD.UI;

/// 모양(Appearance) 라이브러리 – Fusion 기본 바디 모양은 "강철 - 새틴"
public static class Appearances
{
    public static readonly (string key, string name, Vector4 color)[] Library =
    [
        ("steel-satin", "강철 - 새틴 (기본)", new(0.72f, 0.74f, 0.77f, 1)),
        ("aluminum-satin", "알루미늄 - 새틴", new(0.82f, 0.83f, 0.85f, 1)),
        ("aluminum-anodized-blue", "알루미늄 - 양극산화 처리 (파랑)", new(0.25f, 0.45f, 0.78f, 1)),
        ("brass", "황동 - 광택", new(0.83f, 0.68f, 0.32f, 1)),
        ("copper", "구리", new(0.80f, 0.50f, 0.32f, 1)),
        ("plastic-red", "플라스틱 - 광택 (빨강)", new(0.80f, 0.16f, 0.14f, 1)),
        ("plastic-blue", "플라스틱 - 광택 (파랑)", new(0.15f, 0.38f, 0.80f, 1)),
        ("plastic-black", "플라스틱 - 무광 (검정)", new(0.17f, 0.18f, 0.20f, 1)),
        ("plastic-white", "플라스틱 - 무광 (흰색)", new(0.93f, 0.93f, 0.92f, 1)),
        ("rubber", "고무 - 검정", new(0.12f, 0.12f, 0.12f, 1)),
        ("wood-oak", "목재 - 오크", new(0.70f, 0.52f, 0.32f, 1)),
        ("glass-clear", "유리 - 투명", new(0.70f, 0.85f, 0.92f, 0.35f)),
    ];

    public static Vector4 Color(string? key) => Library.FirstOrDefault(a => a.key == key).color is { W: > 0 } c ? c : Library[0].color;
}

public partial class MainWindow
{
    /// A: 모양 – 선택한 바디(없으면 모든 바디)에 모양 적용
    async Task ShowAppearance()
    {
        var targets = Scene.SelectedBodies.Concat(Scene.SelectedFaces.Select(f => f.Body)).Distinct().ToList();
        if (targets.Count == 0) targets = Scene.Bodies.Select(b => b.Index).ToList();
        if (targets.Count == 0) { ShowToast("모양을 적용할 바디가 없습니다."); return; }
        string? chosen = null;
        var list = new StackPanel { Spacing = 2 };
        var w = new Window
        {
            Title = "모양", Width = 340, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(12), Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = $"이 디자인에서 – 바디 {targets.Count}개에 적용할 모양을 선택하십시오.", TextWrapping = TextWrapping.Wrap, FontSize = 12 }, list } } },
        };
        foreach (var (key, name, c) in Appearances.Library)
        {
            var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb((byte)(c.W * 255), (byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255))), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { swatch, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 } } },
            };
            var k = key;
            b.Click += (_, _) => { chosen = k; w.Close(); };
            list.Children.Add(b);
        }
        await w.ShowDialog(this);
        if (chosen == null) return;
        Snapshot();
        foreach (var t in targets) _doc.Appearances[t.ToString()] = chosen;
        _dirty = true; UpdateTitle();
        Scene.BodyColors = Scene.Bodies.ToDictionary(b => b.Index, b => Appearances.Color(_doc.Appearances.GetValueOrDefault(b.Index.ToString())));
        Viewport.Invalidate();
    }
}
