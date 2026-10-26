using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenFusionCAD.UI;

/// 사용자 설정 (~/.config/openfusioncad/settings.json)
public sealed class Settings
{
    public string ApsClientId { get; set; } = "";
    public int ApsCallbackPort { get; set; } = 8765;

    public static string ConfigDir
    {
        get
        {
            var x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var b = string.IsNullOrEmpty(x) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : x;
            return Path.Combine(b, "openfusioncad");
        }
    }

    static string FilePath => Path.Combine(ConfigDir, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        var env = Environment.GetEnvironmentVariable("OFC_APS_CLIENT_ID");
        if (!string.IsNullOrEmpty(env)) s.ApsClientId = env;
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string RedirectUri => $"http://localhost:{ApsCallbackPort}/oauth/callback";
}

/// 간단한 모달 대화상자
public static class Dialogs
{
    static Window Shell(string title, Control body, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new Border { Padding = new Thickness(18, 16), Child = body },
    };

    /// 버튼 중 하나를 고르게 한다. 반환: 누른 버튼 인덱스 (창을 닫으면 -1)
    public static async Task<int> Ask(Window owner, string title, string message, params string[] buttons)
    {
        int result = -1;
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(row);
        var w = Shell(title, panel);
        for (int i = 0; i < buttons.Length; i++)
        {
            int k = i;
            var b = new Button { Content = buttons[i], Classes = { i == 0 ? "primary" : "secondary" } };
            b.Click += (_, _) => { result = k; w.Close(); };
            row.Children.Add(b);
        }
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "")
    {
        string? result = null;
        var tb = new TextBox { Text = initial };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(tb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "확인", Classes = { "primary" } };
        var cancel = new Button { Content = "취소", Classes = { "secondary" } };
        row.Children.Add(ok); row.Children.Add(cancel);
        panel.Children.Add(row);
        var w = Shell(title, panel, 360);
        ok.Click += (_, _) => { result = tb.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { result = tb.Text; w.Close(); } };
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    /// Autodesk 계정 연동(APS) 설정
    public static async Task<bool> ApsSetup(Window owner, Settings s)
    {
        bool saved = false;
        var id = new TextBox { Text = s.ApsClientId, Watermark = "APS 앱의 Client ID" };
        var port = new NumericUpDown { Value = s.ApsCallbackPort, Minimum = 1024, Maximum = 65535, FormatString = "0" };
        var callback = new SelectableTextBlock { Text = s.RedirectUri, FontFamily = new FontFamily("DejaVu Sans Mono, monospace"), FontSize = 12 };
        port.ValueChanged += (_, _) => callback.Text = $"http://localhost:{(int)(port.Value ?? 8765)}/oauth/callback";
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            Text = "OpenFusionCAD 는 Autodesk 가 외부 앱에 제공하는 공식 로그인(Autodesk Platform Services, OAuth PKCE)으로 계정을 연동합니다.\n\n" +
                   "1. https://aps.autodesk.com 에서 애플리케이션을 만듭니다 (유형: Desktop, Mobile, Single-Page App).\n" +
                   "2. Callback URL 에 아래 주소를 등록합니다.\n" +
                   "3. API 목록에서 Data Management API 를 사용하도록 설정합니다.\n" +
                   "4. 발급된 Client ID 를 입력합니다. (Client Secret 은 필요 없습니다)",
        });
        panel.Children.Add(new TextBlock { Text = "Callback URL", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        panel.Children.Add(callback);
        panel.Children.Add(new TextBlock { Text = "Client ID", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        panel.Children.Add(id);
        panel.Children.Add(new TextBlock { Text = "로컬 콜백 포트", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        panel.Children.Add(port);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "저장", Classes = { "primary" } };
        var cancel = new Button { Content = "취소", Classes = { "secondary" } };
        row.Children.Add(ok); row.Children.Add(cancel);
        panel.Children.Add(row);
        var w = Shell("Autodesk 계정 연동 설정", panel, 520);
        ok.Click += (_, _) =>
        {
            s.ApsClientId = (id.Text ?? "").Trim();
            s.ApsCallbackPort = (int)(port.Value ?? 8765);
            s.Save();
            saved = true;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return saved;
    }
}
