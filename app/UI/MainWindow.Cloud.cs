using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenFusionCAD.Cloud;

namespace OpenFusionCAD.UI;

/// 데이터 패널과 사용자 메뉴 – Autodesk 계정(APS) 연동
public partial class MainWindow
{
    Settings _settings = Settings.Load();
    ApsSession? _aps;
    CancellationTokenSource? _signInCts;
    readonly Stack<(string title, Func<Task<List<ApsSession.Node>>> load)> _dataNav = new();

    void HookCloud()
    {
        DataPanelClose.Click += (_, _) => DataPanel.IsVisible = false;
        DataBack.Click += async (_, _) => { if (_dataNav.Count > 1) { _dataNav.Pop(); await ShowDataLevel(); } };
        UserButton.Flyout = UserMenu;
        UserMenu.Opening += (_, _) => BuildUserMenu();
        CreateSession();
        UpdateAccountUi();
        if (_aps?.SignedIn == true) _ = Task.Run(async () =>
        {
            try { await _aps.LoadProfile(); }
            catch { }
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAccountUi);
        });
    }

    void CreateSession() => _aps = new ApsSession(Settings.ConfigDir, _settings.ApsClientId, _settings.ApsCallbackPort);

    void UpdateAccountUi()
    {
        bool on = _aps?.SignedIn == true;
        var name = _aps?.UserName ?? (on ? "Autodesk 계정" : null);
        UserButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Icons.Make("user", 18), new TextBlock { Text = name ?? "로그인", FontSize = 12, VerticalAlignment = VerticalAlignment.Center } },
        };
        CloudText.Text = on ? "Autodesk 연결됨" : "오프라인";
        CloudText.Foreground = new SolidColorBrush(Color.Parse(on ? "#1E6B3D" : "#5B616B"));
        DataAccount.Text = on ? $"{name}{(_aps?.Email != null ? $" · {_aps.Email}" : "")}" : "로그인하지 않음";
    }

    void BuildUserMenu()
    {
        UserMenu.Items.Clear();
        void Add(string h, Func<Task> a) { var m = new MenuItem { Header = h }; m.Click += async (_, _) => await a(); UserMenu.Items.Add(m); }
        if (_aps?.SignedIn == true)
        {
            UserMenu.Items.Add(new MenuItem { Header = $"{_aps.UserName}  {_aps.Email}", IsEnabled = false });
            Add("데이터 패널 열기", () => { if (!DataPanel.IsVisible) ToggleDataPanel(); return Task.CompletedTask; });
            Add("로그아웃", () => { _aps.SignOut(); UpdateAccountUi(); _dataNav.Clear(); DataList.Children.Clear(); ShowToast("로그아웃했습니다."); return Task.CompletedTask; });
        }
        else Add("Autodesk 계정으로 로그인", SignIn);
        Add("계정 연동 설정...", async () => { if (await Dialogs.ApsSetup(this, _settings)) { CreateSession(); UpdateAccountUi(); } });
    }

    async Task SignIn()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApsClientId))
        {
            if (!await Dialogs.ApsSetup(this, _settings) || string.IsNullOrWhiteSpace(_settings.ApsClientId)) return;
            CreateSession();
        }
        _signInCts?.Cancel();
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ShowToast("브라우저에서 Autodesk 로그인을 진행하십시오…", 10);
        try
        {
            await _aps!.SignInAsync(_signInCts.Token);
            UpdateAccountUi();
            ShowToast($"{_aps.UserName} 계정으로 로그인했습니다.");
            if (DataPanel.IsVisible) await LoadRoot();
        }
        catch (OperationCanceledException) { ShowToast("로그인이 취소되었거나 시간이 초과되었습니다."); }
        catch (Exception ex) { ShowToast($"로그인 실패: {ex.Message}", 8); }
    }

    async void ToggleDataPanel()
    {
        DataPanel.IsVisible = !DataPanel.IsVisible;
        if (DataPanel.IsVisible && _dataNav.Count == 0) await LoadRoot();
    }

    async Task LoadRoot()
    {
        _dataNav.Clear();
        DataList.Children.Clear();
        if (_aps?.SignedIn != true)
        {
            DataPath.Text = "";
            var info = new TextBlock { Text = "Autodesk 계정으로 로그인하면 클라우드 프로젝트의 파일을 열 수 있습니다.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Avalonia.Thickness(6) };
            var login = new Button { Content = "Autodesk 계정으로 로그인", Classes = { "primary" }, Margin = new Avalonia.Thickness(6) };
            login.Click += async (_, _) => await SignIn();
            DataList.Children.Add(info);
            DataList.Children.Add(login);
            var recent = Recent();
            if (recent.Count > 0)
            {
                DataList.Children.Add(new TextBlock { Text = "최근 문서", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Avalonia.Thickness(6, 14, 6, 4) });
                foreach (var r in recent) DataList.Children.Add(DataRow("file", Path.GetFileName(r), async () => { if (await ConfirmDiscard()) await OpenPath(r); }));
            }
            return;
        }
        _dataNav.Push(("허브", () => _aps.Hubs()));
        await ShowDataLevel();
    }

    async Task ShowDataLevel()
    {
        if (_dataNav.Count == 0) return;
        var (title, load) = _dataNav.Peek();
        DataPath.Text = string.Join(" / ", _dataNav.Reverse().Select(n => n.title));
        DataList.Children.Clear();
        DataList.Children.Add(new TextBlock { Text = "불러오는 중…", FontSize = 12, Margin = new Avalonia.Thickness(6), Foreground = Brushes.Gray });
        List<ApsSession.Node> nodes;
        try { nodes = await load(); }
        catch (Exception ex)
        {
            DataList.Children.Clear();
            DataList.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Avalonia.Thickness(6) });
            UpdateAccountUi();
            return;
        }
        DataList.Children.Clear();
        if (nodes.Count == 0) DataList.Children.Add(new TextBlock { Text = "(비어 있음)", Foreground = Brushes.Gray, Margin = new Avalonia.Thickness(6) });
        foreach (var n in nodes)
        {
            var node = n;
            switch (n.Kind)
            {
                case "hub":
                    DataList.Children.Add(DataRow("cloud", n.Name, async () => { _dataNav.Push((node.Name, () => _aps!.Projects(node.Id))); await ShowDataLevel(); }));
                    break;
                case "project":
                    DataList.Children.Add(DataRow("folder", n.Name, async () => { _dataNav.Push((node.Name, () => _aps!.TopFolders(node.HubId!, node.Id))); await ShowDataLevel(); }));
                    break;
                case "folder":
                    DataList.Children.Add(DataRow("folder", n.Name, async () => { _dataNav.Push((node.Name, () => _aps!.FolderContents(node.ProjectId!, node.Id))); await ShowDataLevel(); }));
                    break;
                default:
                    bool supported = n.FileType is "step" or "stp" or "iges" or "igs" or "ofcd" or "brep";
                    DataList.Children.Add(DataRow(supported ? "file" : "body", n.Name + (supported ? "" : $"  ({n.FileType})"), async () => await OpenCloudItem(node), dim: !supported));
                    break;
            }
        }
    }

    async Task OpenCloudItem(ApsSession.Node n)
    {
        if (n.FileType is "f3d" or "f3z")
        {
            ShowToast("Fusion 디자인(.f3d)은 Autodesk 전용 형식이라 열 수 없습니다. Fusion 에서 STEP 으로 내보낸 파일을 사용하십시오.", 8);
            return;
        }
        if (n.FileType is not ("step" or "stp" or "iges" or "igs" or "ofcd" or "brep") || n.StorageId == null)
        {
            ShowToast($"지원하지 않는 형식입니다: {n.FileType}");
            return;
        }
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "openfusioncad", "cloud");
        var safe = string.Concat(n.Name.Split(Path.GetInvalidFileNameChars()));
        var dest = Path.Combine(cache, safe.Contains('.') ? safe : $"{safe}.{n.FileType}");
        ShowToast($"내려받는 중: {n.Name}");
        try
        {
            await _aps!.Download(n.StorageId, dest);
            if (n.FileType == "ofcd") { if (await ConfirmDiscard()) await OpenPath(dest); }
            else
            {
                var choice = await Dialogs.Ask(this, n.Name, "이 파일을 어떻게 열까요?", "현재 디자인에 삽입", "새 디자인으로 열기", "취소");
                if (choice == 0) await InsertFile(dest);
                else if (choice == 1 && await ConfirmDiscard()) await OpenPath(dest);
            }
        }
        catch (Exception ex) { ShowToast($"내려받기 실패: {ex.Message}", 8); }
    }

    Control DataRow(string icon, string text, Func<Task> click, bool dim = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(Icons.Make(icon, 18));
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(dim ? "#9AA0A8" : "#2B2F36")), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 });
        var row = new Border { Classes = { "datarow" }, Child = sp };
        row.PointerPressed += async (_, _) => await click();
        return row;
    }
}
