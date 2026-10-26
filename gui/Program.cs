using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;

namespace OpenFusionCAD;

/// <summary>
/// 원본 SFX 의 RunProgram:
///   streamer.exe -a 73e72ada57b7480280f7a6f4a289729f -s production -c https://dl.appstreaming.autodesk.com/production/
/// 와 같은 기본값/인자를 받아 엔진 요청(JSON)으로 변환한다.
/// </summary>
public static class Program
{
    public const string FusionAppId = "73e72ada57b7480280f7a6f4a289729f";

    public static JsonObject Request { get; private set; } = new();
    public static string ResourcesDir { get; private set; } = "";
    public static string Process => Request["process"]?.GetValue<string>() ?? "deploy";
    public static string AppId => Request["app_id"]?.GetValue<string>() ?? FusionAppId;

    [STAThread]
    public static int Main(string[] args)
    {
        ResourcesDir = FindResources();
        Request = ParseArgs(args);
        Request["resources_dir"] = ResourcesDir;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                // 원본: Tahoma/Arial/'Open Sans' – Linux 에 없는 글꼴은 대체 글꼴 사용
                DefaultFamilyName = "avares://FusionClientDownloader/Assets/Fonts#Open Sans",
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK KR") },
                    new FontFallback { FontFamily = new FontFamily("DejaVu Sans") },
                },
            })
            .LogToTrace();

    static string FindResources()
    {
        var env = Environment.GetEnvironmentVariable("OFC_RESOURCES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "resources");
            if (File.Exists(Path.Combine(cand, "precheck_config.json"))) return cand;
            dir = dir.Parent;
        }
        return "/opt/openfusioncad/resources";
    }

    /// <summary>원본 streamer.exe 와 같은 명령행 인자 해석</summary>
    static JsonObject ParseArgs(string[] args)
    {
        var r = new JsonObject
        {
            ["process"] = "deploy",
            ["app_id"] = FusionAppId,
            ["stream"] = "production",
        };
        var wine = new JsonObject();
        var launchArgs = new JsonArray();
        var map = new Dictionary<string, string>
        {
            ["-p"] = "process", ["--process"] = "process",
            ["-a"] = "app_id", ["--appid"] = "app_id",
            ["-s"] = "stream", ["--stream"] = "stream",
            ["-c"] = "conn", ["--conn"] = "conn",
            ["--lang"] = "lang",
            ["--installlocation"] = "install_location",
            ["--pull-dir"] = "pull_dir",
        };
        var flags = new Dictionary<string, string>
        {
            ["--quiet"] = "quiet",
            ["--no-auto-launch"] = "no_auto_launch", ["-nal"] = "no_auto_launch",
            ["--full-deploy"] = "full_deploy",
            ["--purge-incomplete"] = "purge", ["-r"] = "purge",
            ["--silent"] = "silent",
            ["--cleancacheall"] = "cleancacheall",
            ["--removeuserdata"] = "removeuserdata",
            ["--removeusercred"] = "removeusercred",
            ["--deep_clean"] = "deep_clean",
            ["--no-desktop-shortcut"] = "desktop_shortcut",
        };
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (map.TryGetValue(a, out var key) && i + 1 < args.Length) { r[key] = args[++i]; continue; }
            if (flags.TryGetValue(a, out var f)) { r[f] = f != "desktop_shortcut"; continue; }
            switch (a)
            {
                case "--threadscount" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): r["threads"] = n; i++; break;
                case "--wine" when i + 1 < args.Length: wine["wine_bin"] = args[++i]; break;
                case "--no-dxvk": wine["dxvk"] = false; break;
                case "--with-webview2": wine["webview2"] = true; break;
                case "--": for (i++; i < args.Length; i++) launchArgs.Add(args[i]); break;
                default: break; // -g, --ppid 등 원본 호환 인자는 무시
            }
        }
        if (wine.Count > 0) r["wine"] = wine;
        if (launchArgs.Count > 0) r["launch_args"] = launchArgs;
        return r;
    }
}
