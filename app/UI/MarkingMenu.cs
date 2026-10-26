using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenFusionCAD.UI;

/// <summary>
/// Fusion 360 식 마킹 메뉴: 캔버스를 오른쪽 클릭한 자리에 8방향 원형 명령 + 아래 넘침 메뉴.
/// 빈 곳을 클릭하거나 Esc 를 누르면 닫힌다.
/// </summary>
public sealed class MarkingMenu : Canvas
{
    public MarkingMenu()
    {
        IsVisible = false;
        Background = Brushes.Transparent; // 바깥 클릭을 받아 닫기
        PointerPressed += (_, e) => { if (e.Source == this) { Hide(); e.Handled = true; } };
    }

    public void Hide() { IsVisible = false; Children.Clear(); }

    public void Show(Point at, (string label, string icon, Action act)[] radial, List<(string label, Action act)> overflow)
    {
        Children.Clear();
        const double R = 92;
        // 가운데 표시
        var hub = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.Parse("#3A3F47")), BorderBrush = Brushes.White, BorderThickness = new Thickness(2) };
        SetLeft(hub, at.X - 7); SetTop(hub, at.Y - 7);
        Children.Add(hub);
        for (int i = 0; i < radial.Length && i < 8; i++)
        {
            var (label, icon, act) = radial[i];
            if (string.IsNullOrEmpty(label)) continue;
            double ang = -Math.PI / 2 + i * Math.PI / 4; // 북쪽부터 시계 방향
            var pill = Pill(label, icon, act);
            pill.Measure(Size.Infinity);
            var sz = pill.DesiredSize;
            double cx = at.X + Math.Cos(ang) * R, cy = at.Y + Math.Sin(ang) * R * 0.78;
            // 좌우에 있는 항목은 중심에서 바깥쪽으로 정렬
            double x = Math.Cos(ang) > 0.3 ? cx - 12 : Math.Cos(ang) < -0.3 ? cx - sz.Width + 12 : cx - sz.Width / 2;
            SetLeft(pill, x); SetTop(pill, cy - sz.Height / 2);
            Children.Add(pill);
        }
        // 넘침 메뉴
        var list = new StackPanel { Spacing = 0 };
        foreach (var (label, act) in overflow)
        {
            var b = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(12, 4), FontSize = 12, CornerRadius = new CornerRadius(0),
            };
            b.Click += (_, _) => { Hide(); act(); };
            list.Children.Add(b);
        }
        var box = new Border
        {
            Child = list, Width = 190, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#B8BDC4")), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #40000000"), Padding = new Thickness(0, 4),
        };
        SetLeft(box, at.X - 95); SetTop(box, at.Y + R * 0.78 + 26);
        Children.Add(box);
        IsVisible = true;
    }

    Border Pill((string, string, Action) _1, string icon, Action act) => throw new NotSupportedException();

    Border Pill(string label, string icon, Action act)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(Icons.Make(icon, 18));
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#2B2F36")) });
        var b = new Border
        {
            Child = sp, Background = new SolidColorBrush(Color.Parse("#F7FFFFFF")), BorderBrush = new SolidColorBrush(Color.Parse("#9AA0A8")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4), Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = BoxShadows.Parse("0 1 4 0 #30000000"),
        };
        b.PointerEntered += (_, _) => b.Background = new SolidColorBrush(Color.Parse("#E4F2FA"));
        b.PointerExited += (_, _) => b.Background = new SolidColorBrush(Color.Parse("#F7FFFFFF"));
        b.PointerPressed += (_, e) => { e.Handled = true; Hide(); act(); };
        return b;
    }
}
