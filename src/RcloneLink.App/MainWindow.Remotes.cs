using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private readonly TextBlock _versionValue = Label("正在检查…", 15, true);
    private readonly TextBlock _winFspValue = Label("正在检查…", 15, true);
    private readonly TextBlock _remoteCount = Label("—", 30, true);
    private readonly TextBlock _mountCount = Label("0", 30, true);
    private readonly TextBlock _profileCount = Label("0", 30, true);
    private readonly TextBlock _healthChecked = Hint("首次启动时自动检查运行环境。");
    private readonly StackPanel _remoteRows = new() { Spacing = 10 };

    private FrameworkElement BuildOverviewPage()
    {
        var page = Page("概览", "连接云存储，让文件像本地磁盘一样触手可及。", out var body);
        var hero = new Grid { ColumnSpacing = 20 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var intro = Stack(Label("从一个远程连接开始", 20, true), Label("添加 WebDAV，或通过 rclone 配置更多云存储。然后挂载磁盘、浏览文件和管理传输。"));
        hero.Children.Add(intro);
        var addRemote = SimpleButton("OverviewAddRemote", "添加远程连接", () => Navigate("remotes"), "\uE710", true);
        Grid.SetColumn(addRemote, 1);
        hero.Children.Add(addRemote);
        body.Children.Add(Card(hero, new Thickness(24)));

        var counts = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++) counts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var countCards = new[]
        {
            Card(Stack(Hint("远程连接"), _remoteCount)),
            Card(Stack(Hint("运行中的挂载"), _mountCount)),
            Card(Stack(Hint("已保存的挂载"), _profileCount))
        };
        for (var i = 0; i < countCards.Length; i++) { Grid.SetColumn(countCards[i], i); counts.Children.Add(countCards[i]); }
        body.Children.Add(counts);

        body.Children.Add(Label("运行环境", 18, true));
        body.Children.Add(Card(Stack(
            StatusRow("\uE756", "rclone", "云存储与文件传输引擎", _versionValue),
            _rcloneUpdate,
            new Border { Height = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 128, 128, 128)) },
            StatusRow("\uEDA2", "WinFsp", "将远程存储挂载为 Windows 磁盘所需的驱动", _winFspValue),
            _winFspUpdate,
            Horizontal(ActionButton("RefreshHealth", "重新检查", RefreshHealthAsync, "\uE72C"),
                ActionButton("CheckUpdates", "检查最新版本", () => ManualUpdateCheckAsync(false)),
                ActionButton("UpdateRclone", "更新 rclone", () => ManualUpdateCheckAsync(true))),
            ActionButton("InstallWinFsp", "安装 / 更新 WinFsp", UpdateWinFspAsync),
            Hint("自动更新 rclone 会保留旧引擎；使用中暂缓。WinFsp 安装需要管理员权限。"),
            _healthChecked)));
        body.Children.Add(Label("开机启动与自动挂载", 18, true));
        body.Children.Add(Card(Stack(_startupStatus, SimpleButton("StartupSettings", "配置启动与挂载", () => Navigate("settings")))));
        body.Children.Add(Label("常用操作", 18, true));
        body.Children.Add(Card(Stack(
            Horizontal(SimpleButton("OverviewMounts", "管理磁盘挂载", () => Navigate("mounts"), "\uEDA2"),
                SimpleButton("OverviewFiles", "浏览云端文件", () => Navigate("files"), "\uE8B7"),
                SimpleButton("OverviewTransfers", "创建传输任务", () => Navigate("transfers"), "\uE8AB")),
            Hint("首次使用：添加远程连接 → 测试连接 → 添加挂载。文件浏览与传输无需安装 WinFsp。"))));
        return page;
    }

    private static Grid StatusRow(string glyph, string title, string description, TextBlock value)
    {
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, FontSize = 24, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var label = Stack(Label(title, 15, true), Hint(description));
        label.Spacing = 4;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.MaxWidth = 300;
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private async Task RefreshHealthAsync()
    {
        var winFsp = NativeIntegration.GetWinFspVersion();
        _winFspValue.Text = winFsp != null ? "WinFsp " + winFsp.ToString(3) : "尚未安装";
        try
        {
            var version = await _service.GetVersionAsync(_lifetime.Token);
            _versionValue.Text = version.Split('\n').FirstOrDefault()?.Trim() ?? version;
            AppendLog("已检测到 " + _versionValue.Text);
        }
        catch (Exception ex)
        {
            _versionValue.Text = "不可用 · 请检查设置";
            AppendLog("rclone 检查失败：" + ex.Message);
            Notify("未能启动 rclone，请在设置中检查可执行文件路径。" + ex.Message, InfoBarSeverity.Warning);
        }
        _healthChecked.Text = $"上次检查：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        RefreshOverviewCounts();
    }

    private void RefreshOverviewCounts()
    {
        _remoteCount.Text = _remotes.Count.ToString();
        _profileCount.Text = _state.Mounts.Count.ToString();
        _mountCount.Text = _mounts.GetStates().Count(s => s.Status == MountStatus.Mounted).ToString();
        RefreshStartupStatus();
    }

    private FrameworkElement BuildRemotesPage()
    {
        var page = Page("远程连接", "管理 rclone 配置中的云存储。连接凭据由 rclone 保存。", out var body);
        body.Children.Add(Horizontal(
            ActionButton("AddWebDavRemote", "添加 WebDAV", AddWebDavAsync, "\uE710", true),
            ActionButton("RefreshRemotes", "刷新", RefreshRemotesAsync, "\uE72C"),
            ActionButton("OpenProviderSetup", "添加其他存储", OpenProviderSetupAsync, "\uE774")));
        body.Children.Add(Hint("OneDrive、Google Drive、S3、SFTP 等提供商均可在应用内设置，需要登录的连接会在浏览器中授权。"));
        body.Children.Add(_remoteRows);
        return page;
    }

    private async Task RefreshRemotesAsync()
    {
        InvalidatePreview();
        _remotes = await _service.ListRemotesAsync(_lifetime.Token);
        _remoteRows.Children.Clear();
        if (_remotes.Count == 0)
        {
            _remoteRows.Children.Add(Card(Stack(new FontIcon { Glyph = "\uE774", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Left },
                Label("还没有远程连接", 20, true), Label("添加一个 WebDAV 连接，或打开配置向导接入其他云存储。"))));
        }
        foreach (var remote in _remotes)
        {
            var name = remote;
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Stack(Label(name, 16, true), Hint(name + ": · rclone 远程连接")));
            var actions = Horizontal(
                ActionButton("TestRemote" + SafeName(name), "测试连接", async () =>
                {
                    RequireSuccess(await _service.CheckConnectionAsync(name, _lifetime.Token), "连接测试");
                    Notify($"已成功访问 {name} 的根目录。");
                }),
                SimpleButton("BrowseRemote" + SafeName(name), "浏览", () => { _browserPath.Text = name + ":"; Navigate("files"); _ = RunUiAsync(LoadFilesAsync, "读取文件"); }),
                ActionButton("DeleteRemote" + SafeName(name), "删除", () => DeleteRemoteAsync(name)));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            _remoteRows.Children.Add(Card(grid));
        }
        RefreshBrowserRemotes();
        RefreshOverviewCounts();
    }

    private static string SafeName(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private async Task AddWebDavAsync()
    {
        var name = TextInput("RemoteName", "连接名称", placeholder: "例如：我的网盘");
        var url = TextInput("RemoteUrl", "WebDAV 地址", placeholder: "https://example.com/dav");
        var user = TextInput("RemoteUsername", "用户名");
        var password = new PasswordBox { Name = "RemotePassword", Header = "密码", PasswordRevealMode = PasswordRevealMode.Peek };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(password, "密码");
        var vendor = SelectInput("RemoteVendor", "WebDAV 提供商", ["other", "nextcloud", "owncloud", "sharepoint", "sharepoint-ntlm", "rclone", "fastmail", "infinitescale"], "other");
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false };
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = "添加 WebDAV 连接", PrimaryButtonText = "添加连接", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, RequestedTheme = _root.RequestedTheme,
            Content = new ScrollViewer { Content = Stack(name, url, user, password, vendor, Hint("密码通过 rclone 的标准机制混淆后存入配置文件，不会显示在日志中。"), error), MaxHeight = 560, MinWidth = 400 }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("请输入连接名称。");
                if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) throw new ArgumentException("请输入以 https:// 或 http:// 开头的 WebDAV 地址。");
                var result = await _service.CreateWebDavAsync(name.Text.Trim(), url.Text.Trim(), user.Text, password.Password, vendor.SelectedItem?.ToString() ?? "other", _lifetime.Token);
                RequireSuccess(result, "添加连接");
                password.Password = "";
            }
            catch (Exception ex) { args.Cancel = true; error.Message = CleanLog(ex.Message); error.IsOpen = true; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RefreshRemotesAsync();
            Notify("远程连接已添加。建议先运行“测试连接”。");
        }
        password.Password = "";
    }

    private async Task DeleteRemoteAsync(string remote)
    {
        if (TransferUsesRemote(remote)) throw new InvalidOperationException("该远程连接正在被传输任务使用，请先取消或等待任务完成。");
        var profiles = _state.Mounts.Where(m => m.Remote.Equals(remote, StringComparison.OrdinalIgnoreCase) || m.Remote.StartsWith(remote + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (profiles.Length > 0) throw new InvalidOperationException($"该连接仍被 {profiles.Length} 个挂载配置引用。请先停止并删除这些挂载配置，再删除连接。");
        if (!await ConfirmAsync("删除远程连接？", $"将从 rclone 配置中删除“{remote}”及其连接凭据。云端文件不会被删除。", "删除连接")) return;
        RequireSuccess(await _service.DeleteRemoteAsync(remote, _lifetime.Token), "删除连接");
        await RefreshRemotesAsync();
        Notify($"已删除连接 {remote}。");
    }
}
