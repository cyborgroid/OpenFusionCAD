using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace OpenFusionCAD;

/// <summary>
/// 원본 processprogressink.js 의 ProcessProgressSink / Utilities 를 C# 로 옮긴 창.
/// 엔진 이벤트(JSON)를 받아 동일한 시각 요소를 갱신하고, 대화상자 결과를 엔진에 돌려준다.
/// </summary>
public partial class MainWindow : Window
{
    // processprogressink.js: infotainmentCountMap = { '<fusion>': { 'Deploy': 7 } }
    readonly string _bgSource;
    readonly int _bgCount;
    string _lastImg = "";
    bool _running;
    bool _closingRequested;
    TaskCompletionSource<string>? _pending;
    readonly DispatcherTimer _indeterminate;
    double _indetX;

    public MainWindow()
    {
        InitializeComponent();
        var res = Program.ResourcesDir;
        var ico = Path.Combine(res, "Fusion.ico");
        if (File.Exists(ico)) Icon = new WindowIcon(ico);
        var close = Path.Combine(res, "html", "res", "close.png");
        if (File.Exists(close)) { ErrorCloseImg.Source = new Bitmap(close); QueryCloseImg.Source = new Bitmap(close); }

        var proc = Program.Process is "deploy" or "update" or "reinstall" ? "Deploy" : "infotainment";
        var deployDir = Path.Combine(res, "html", "res", proc, Program.AppId);
        _bgSource = Directory.Exists(deployDir) ? proc : "infotainment";
        _bgCount = _bgSource == "Deploy" ? CountImages(Path.Combine(res, "html", "res", "Deploy", Program.AppId)) : 0;
        SetBackground(BgUrl(0));

        // $("#progressbar").progressbar({ value: false }) – 초기 불확정 상태
        _indeterminate = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            _indetX = (_indetX + 4) % 340;
            BarIndeterminate.Margin = new Thickness(_indetX - 80, 0, 0, 0);
        });
        SetIndeterminate(true);

        MakeDraggable(ErrorTitlebar, ErrorDialog);
        MakeDraggable(QueryTitlebar, QueryDialog);
        Opened += (_, _) => Start();
        Closing += OnClosing;
    }

    static int CountImages(string dir)
    {
        int n = 0;
        while (File.Exists(Path.Combine(dir, $"{n}.png"))) n++;
        return n;
    }

    string BgUrl(int idx) => Path.Combine(Program.ResourcesDir, "html", "res", _bgSource, Program.AppId, $"{idx}.png");

    void SetBackground(string path)
    {
        if (File.Exists(path)) Bg.Source = new Bitmap(path);
    }

    void SetIndeterminate(bool on)
    {
        BarIndeterminate.IsVisible = on;
        if (on) _indeterminate.Start(); else _indeterminate.Stop();
    }

    // ------------------------------------------------------------------ engine

    void Start()
    {
        if (Program.Demo is { } demo) { RunDemo(demo); return; }
        _running = true;
        var req = Program.Request.ToJsonString();
        new Thread(() =>
        {
            int code;
            try { code = NativeCore.Run(req, OnEngineEvent); }
            catch (DllNotFoundException e)
            {
                Console.Error.WriteLine(e.Message);
                code = 3;
            }
            _running = false;
            Dispatcher.UIThread.Post(() =>
            {
                // 원본: streamer 프로세스 종료와 함께 창이 닫힌다
                Environment.ExitCode = code;
                Close();
            });
        }) { IsBackground = true, Name = "ofc-engine" }.Start();
    }

    /// 엔진 스레드에서 호출됨. 응답이 필요한 이벤트는 UI 결과를 기다려 반환한다.
    string? OnEngineEvent(string json)
    {
        var ev = JsonNode.Parse(json)!.AsObject();
        var type = ev["type"]!.GetValue<string>();
        if (type == "progress")
        {
            Dispatcher.UIThread.Post(() => Progress(ev));
            return null;
        }
        if (type == "complete") return null;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        Dispatcher.UIThread.Post(() =>
        {
            switch (type)
            {
                case "reporterror": ShowErrorDialog(ev, tcs); break;
                case "promptuser": ShowQuery(S(ev, "title"), S(ev, "body"), S(ev, "primary"), S(ev, "secondary"), tcs); break;
                case "informuser": ShowQuery(S(ev, "title"), S(ev, "body"), S(ev, "primary"), null, tcs); break;
                case "precheckfailure": ShowPrecheck(ev, tcs); break;
                case "provideproxycreds": ShowProxyLogin(S(ev, "summary"), S(ev, "message"), tcs); break;
                case "abortuninstall": ShowSurvey(ev, tcs); break;
                default: tcs.TrySetResult(""); break;
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    static string S(JsonObject o, string k) => o[k]?.GetValue<string>() ?? "";

    /// 개발·검증용: 엔진 없이 각 화면 상태를 재현 (--demo progress|error|query|precheck|survey|proxy)
    void RunDemo(string demo)
    {
        new Thread(() =>
        {
            string Ev(string js) => OnEngineEvent(js) ?? "";
            Ev("{\"type\":\"progress\",\"step\":3,\"step_total\":5,\"description\":\"Downloading Autodesk Fusion  (612 / 1509 MB)\",\"percentage\":41.5,\"update_bg\":true}");
            Thread.Sleep(1500);
            var r = demo switch
            {
                "error" => Ev("{\"type\":\"reporterror\",\"title\":\"Problem installing Fusion\",\"what_label\":\"What's wrong?\",\"what\":\"Network failure.  Unable to recover.\",\"detail\":\"Connection reset by peer, url:https://dl.appstreaming.autodesk.com/production/packages/xxx.tar.xz\",\"todo_label\":\"What should I do?\",\"todo\":\"Make sure your internet is working normally or switch to a more stable connection, then run the installer again.\",\"log_dir\":\"/tmp\",\"log_file\":\"/tmp/x.log\",\"primary\":\"OK\",\"secondary\":\"Open Log File Location\"}"),
                "query" => Ev("{\"type\":\"promptuser\",\"title\":\"Latest version already installed\",\"body\":\"You have the latest version of Autodesk Fusion.\\n\\nWould you like to start the application?\",\"primary\":\"Yes\",\"secondary\":\"No\"}"),
                "precheck" => Ev("{\"type\":\"precheckfailure\",\"paragraph\":\"The installer could not continue because of the following issues:\",\"items\":[{\"description\":\"Wine is not installed or too old (9.0 or later is required).\",\"url\":\"https://gitlab.winehq.org\"},{\"description\":\"System is out of disk space. Please create 8 GB of space before proceeding.\",\"url\":\"https://knowledge.autodesk.com\"}],\"hyperlink\":\"Learn more..\",\"btn_retest\":\"Re-Test\",\"btn_cancel\":\"Cancel\"}"),
                "survey" => Ev("{\"type\":\"abortuninstall\",\"title\":\"We are sorry to see you go.\",\"continue_label\":\"Uninstall\",\"cancel_label\":\"Cancel\"}"),
                "proxy" => Ev("{\"type\":\"provideproxycreds\",\"summary\":\"Proxy authentication required\",\"message\":\"Enter the user name and password for the proxy server.\"}"),
                _ => "",
            };
            Console.WriteLine($"demo result: {r}");
            for (int i = 42; i <= 100; i += 2)
            {
                Ev($"{{\"type\":\"progress\",\"step\":3,\"step_total\":5,\"description\":\"Installing Autodesk Fusion\",\"percentage\":{i},\"update_bg\":true}}");
                Thread.Sleep(120);
            }
            Dispatcher.UIThread.Post(Close);
        }) { IsBackground = true }.Start();
    }

    // ------------------------------------------------------------------ progress

    /// ProcessProgressSink.progress(step, description, step_total, step_percentage, updatebgimage)
    void Progress(JsonObject ev)
    {
        var pct = ev["percentage"]?.GetValue<double>() ?? 0;
        var desc = S(ev, "description");
        ProgressDescription.Text = string.IsNullOrEmpty(desc) ? " " : desc;
        var intVal = (int)pct;
        ProgressLabel.Text = $"{intVal}%";
        SetIndeterminate(false);
        BarValue.Width = Math.Max(0, (BarTrack.Bounds.Width > 0 ? BarTrack.Bounds.Width - 2 : 256) * intVal / 100.0);

        if (_bgCount > 0)
        {
            var imgId = Math.Min(_bgCount - 1, (int)Math.Floor(_bgCount * (pct / 100)));
            var img = BgUrl(imgId);
            if (img != _lastImg)
            {
                _lastImg = img;
                if (ev["update_bg"]?.GetValue<bool>() == false) return;
                CrossFade(img);
            }
        }
    }

    async void CrossFade(string img)
    {
        if (!File.Exists(img)) return;
        // #backbuff 에 다음 이미지를 놓고 1200ms 동안 fadeIn 후 교체
        BackBuff.Source = new Bitmap(img);
        BackBuff.Opacity = 0;
        BackBuff.IsVisible = true;
        const int steps = 40;
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(1200 / steps);
            BackBuff.Opacity = i / (double)steps;
        }
        Bg.Source = BackBuff.Source;
        BackBuff.IsVisible = false;
    }

    // ------------------------------------------------------------------ dialogs

    void Mask(bool on) => BodyMask.IsVisible = on;

    static void Once(Button b, Action a)
    {
        void H(object? s, RoutedEventArgs e) { b.Click -= H; a(); }
        b.Click += H;
    }

    /// Utilities.showErrorDialog
    void ShowErrorDialog(JsonObject ev, TaskCompletionSource<string> tcs)
    {
        ErrorTitle.Text = S(ev, "title");
        Col11.Text = S(ev, "what_label");
        Col12.Text = S(ev, "what");
        Col12Detail.Text = S(ev, "detail");
        Col21.Text = S(ev, "todo_label");
        Col22.Text = S(ev, "todo");
        BtnOK.Content = S(ev, "primary");
        BtnDir.Content = S(ev, "secondary");
        var logDir = S(ev, "log_dir");
        BtnDir.IsEnabled = Directory.Exists(logDir);
        AdjustHeaderHeight(ErrorTitlebar, ErrorTitle);
        Mask(true);
        ErrorDialog.IsVisible = true;

        void Done()
        {
            BtnDir.Click -= OpenDir;
            ErrorDialog.IsVisible = false;
            Mask(false);
            tcs.TrySetResult("OK");
        }
        void OpenDir(object? s, RoutedEventArgs e) => OpenExternal(logDir); // callNative('3', LogDir, LogFile)
        BtnDir.Click += OpenDir;
        Once(BtnOK, Done);
        Once(ErrorClose, Done);
    }

    /// Utilities.promptUser / informUser
    void ShowQuery(string title, string body, string primary, string? secondary, TaskCompletionSource<string> tcs)
    {
        QueryTitle.Text = title;
        QueryBody.Text = body;
        BtnYes.Content = primary;
        BtnNo.IsVisible = secondary != null;
        if (secondary != null) BtnNo.Content = secondary;
        AdjustHeaderHeight(QueryTitlebar, QueryTitle);
        Mask(true);
        QueryDialog.IsVisible = true;
        void Finish(string r)
        {
            QueryDialog.IsVisible = false;
            Mask(false);
            tcs.TrySetResult(r);
        }
        Once(BtnYes, () => Finish("Yes"));
        Once(BtnNo, () => Finish("No"));
        Once(QueryClose, () => Finish("No"));
    }

    /// Utilities.adjustHeaderHeight – 제목이 382px 를 넘으면 제목줄 108px
    static void AdjustHeaderHeight(Border bar, TextBlock title)
    {
        var ft = new FormattedText(title.Text ?? "", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(title.FontFamily), 22, Brushes.Black);
        bar.Height = ft.Width > 382 ? 108 : 78;
    }

    /// Utilities.precheckfailure
    void ShowPrecheck(JsonObject ev, TaskCompletionSource<string> tcs)
    {
        PrecheckParagraph.Text = S(ev, "paragraph");
        RetestButton.Content = S(ev, "btn_retest");
        CancelButton.Content = S(ev, "btn_cancel");
        var link = S(ev, "hyperlink");
        PrecheckRows.Children.Clear();
        foreach (var item in ev["items"]?.AsArray() ?? new JsonArray())
        {
            var o = item!.AsObject();
            var url = S(o, "url");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("4*,*") };
            row.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#898989")), Padding = new Thickness(5),
                Child = new TextBlock { Text = S(o, "description"), Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            });
            var a = new Button
            {
                Content = new TextBlock { Text = link, Foreground = Brushes.Blue, FontSize = 12, TextDecorations = TextDecorations.Underline },
                Background = Brushes.Transparent, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand), IsVisible = !string.IsNullOrEmpty(url),
            };
            a.Click += (_, _) => OpenExternal(url);
            var cell = new Border { Background = new SolidColorBrush(Color.Parse("#898989")), Padding = new Thickness(5), Child = a };
            Grid.SetColumn(cell, 1);
            row.Children.Add(cell);
            PrecheckRows.Children.Add(row);
        }
        PrecheckPanel.IsVisible = true;
        Once(RetestButton, () => { PrecheckPanel.IsVisible = false; tcs.TrySetResult("retest"); });
        Once(CancelButton, () => { PrecheckPanel.IsVisible = false; tcs.TrySetResult("cancel"); });
    }

    /// Utilities.showEmbeddedSurvey
    void ShowSurvey(JsonObject ev, TaskCompletionSource<string> tcs)
    {
        SurveyTitle.Text = S(ev, "title");
        SurveyContinue.Content = S(ev, "continue_label");
        SurveyCancel.Content = S(ev, "cancel_label");
        SurveyPanel.IsVisible = true;
        Once(SurveyContinue, () => { SurveyPanel.IsVisible = false; tcs.TrySetResult("Yes"); });
        Once(SurveyCancel, () => { SurveyPanel.IsVisible = false; tcs.TrySetResult("No"); });
    }

    /// Utilities.loginDialog
    void ShowProxyLogin(string summary, string message, TaskCompletionSource<string> tcs)
    {
        ProxyTitle.Text = summary;
        ProxyMessage.Text = message;
        ProxyUser.Text = ""; ProxyPass.Text = "";
        Mask(true);
        ProxyDialog.IsVisible = true;
        ProxyUser.Focus();
        void Finish(string r) { ProxyDialog.IsVisible = false; Mask(false); tcs.TrySetResult(r); }
        Once(ProxyOk, () => Finish(JsonSerializer.Serialize(new JsonObject { ["username"] = ProxyUser.Text ?? "", ["password"] = ProxyPass.Text ?? "" })));
        Once(ProxyCancel, () => Finish("Cancel"));
    }

    /// 제목줄 드래그로 대화상자 이동 (원본 .dlg-titlebar mousedown/mousemove)
    void MakeDraggable(Control handle, Control dialog)
    {
        Point? start = null;
        handle.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null) return;
            start = e.GetPosition(dialog);
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (start is not { } s) return;
            var p = e.GetPosition(Root);
            Canvas.SetLeft(dialog, p.X - s.X);
            Canvas.SetTop(dialog, p.Y - s.Y);
        };
        handle.PointerReleased += (_, e) => { start = null; e.Pointer.Capture(null); };
    }

    /// window.external.callNative('1', url) / ('3', dir) → xdg-open
    static void OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try { Process.Start(new ProcessStartInfo("xdg-open", $"\"{target}\"") { UseShellExecute = false }); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); }
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_running || _closingRequested) return;
        // 작업 중 창 닫기 → 엔진 취소 요청 후 종료 대기 (최대 5초)
        _closingRequested = true;
        e.Cancel = true;
        NativeCore.ofc_cancel();
        _pending?.TrySetResult("Cancel"); // 대기 중인 대화상자는 '취소'로 응답
        ProgressDescription.Text = "…";
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 50 && _running; i++) await Task.Delay(100);
            Dispatcher.UIThread.Post(() => { _running = false; Close(); });
        });
    }
}

static class VisualExt
{
    public static T? FindAncestorOfType<T>(this Visual v) where T : class
    {
        Visual? cur = v;
        while (cur != null)
        {
            if (cur is T t) return t;
            cur = cur.GetVisualParent();
        }
        return null;
    }

    static Visual? GetVisualParent(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
}
