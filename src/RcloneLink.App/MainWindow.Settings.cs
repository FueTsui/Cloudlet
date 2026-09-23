using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private FrameworkElement BuildSettingsPage()
    {
        var page = Page("设置", "调整运行环境、外观与 Windows 集成。", out var body);
        var rclone = TextInput("SettingsRclonePath", "rclone 可执行文件", _state.Settings.RclonePath);
        var config = TextInput("SettingsConfigPath", "rclone 配置文件", _state.Settings.ConfigPath);
        var theme = SelectInput("SettingsTheme", "应用主题", ["跟随 Windows", "浅色", "深色"], _state.Settings.Theme switch { "Light" => "浅色", "Dark" => "深色", _ => "跟随 Windows" });
        var tray = Toggle("SettingsTray", "显示系统托盘图标", _state.Settings.TrayVisible);
        var closeToTray = Toggle("SettingsCloseToTray", "关闭窗口时隐藏到托盘", _state.Settings.CloseToTray);
        var startup = Toggle("SettingsStartup", "登录 Windows 时启动", _state.Settings.StartWithWindows);
        var autoMount = Toggle("SettingsAutoMount", "启动后自动挂载", _state.Settings.AutoMount);
        body.Children.Add(Label("运行环境", 18, true));
        body.Children.Add(Card(Stack(
            SettingsPathRow(rclone, "PickRclonePath", async () =>
            {
                var selected = await NativeIntegration.PickFileAsync(this, "选择 rclone.exe", ".exe");
                if (selected != null) rclone.Text = selected;
            }),
            SettingsPathRow(config, "PickConfigPath", async () =>
            {
                var selected = await NativeIntegration.PickFileAsync(this, "选择 rclone 配置文件", ".conf");
                if (selected != null) config.Text = selected;
            }),
            Hint("支持已有 rclone.conf。路径不存在时，添加连接将创建配置文件。修改运行路径前请停止挂载与传输。"))));
        body.Children.Add(Label("外观与启动", 18, true));
        body.Children.Add(Card(Stack(theme, TwoColumns(tray, closeToTray), TwoColumns(startup, autoMount),
            Hint("关闭窗口时隐藏到托盘，需要启用托盘图标。自动挂载还需在每个挂载配置中开启“随应用自动挂载”。"))));
        body.Children.Add(ActionButton("SaveSettings", "保存设置", async () =>
        {
            var rclonePath = rclone.Text.Trim().Trim('"');
            var configPath = config.Text.Trim().Trim('"');
            if (!File.Exists(rclonePath)) throw new ArgumentException("找不到 rclone 可执行文件，请检查路径。");
            if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathFullyQualified(configPath)) throw new ArgumentException("配置文件必须使用完整路径。");
            rclonePath = Path.GetFullPath(rclonePath);
            configPath = Path.GetFullPath(configPath);
            var pathsChanged = !rclonePath.Equals(_state.Settings.RclonePath, StringComparison.OrdinalIgnoreCase) || !configPath.Equals(_state.Settings.ConfigPath, StringComparison.OrdinalIgnoreCase);
            if (pathsChanged && ProviderWizardActive) throw new InvalidOperationException("请先完成或取消正在设置的存储连接，再修改运行环境路径。");
            if (pathsChanged && (_activeTransfer != null || _mounts.GetStates().Any(s => s.Status is MountStatus.Starting or MountStatus.Mounted or MountStatus.Stopping)))
                throw new InvalidOperationException("请先停止全部挂载和传输任务，再修改运行环境路径。");
            if (closeToTray.IsOn && !tray.IsOn) throw new ArgumentException("关闭窗口时隐藏到托盘需要显示托盘图标。请开启托盘图标，或关闭“关闭窗口时隐藏到托盘”。");
            NativeIntegration.SetStartup(startup.IsOn);
            _state.Settings.RclonePath = rclonePath;
            _state.Settings.ConfigPath = configPath;
            _state.Settings.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            _state.Settings.TrayVisible = tray.IsOn;
            _state.Settings.CloseToTray = closeToTray.IsOn;
            _state.Settings.StartWithWindows = startup.IsOn;
            _state.Settings.AutoMount = autoMount.IsOn;
            SaveState();
            ApplyTheme();
            await RefreshHealthAsync();
            if (pathsChanged) { InvalidatePreview(); await RefreshRemotesAsync(); }
            Notify("设置已保存。");
        }, "\uE74E", true));
        body.Children.Add(Label("诊断与帮助", 18, true));
        body.Children.Add(Card(Stack(
            Horizontal(SimpleButton("OpenAppData", "打开应用数据目录", () => NativeIntegration.OpenPath(Path.GetDirectoryName(_store.StatePath)!)),
                SimpleButton("OpenDocumentation", "rclone 官方文档", () => NativeIntegration.OpenUrl("https://rclone.org/docs/")),
                SimpleButton("OpenMountDocumentation", "挂载参数说明", () => NativeIntegration.OpenUrl("https://rclone.org/commands/rclone_mount/"))),
            new Expander
            {
                Header = "本次会话日志", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = Stack(_applicationLog, SimpleButton("CopyApplicationLog", "复制日志", () =>
                {
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    data.SetText(_applicationLog.Text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                    Notify("会话日志已复制。");
                }))
            })));
        body.Children.Add(Label("关于 Cloudlet", 18, true));
        body.Children.Add(Card(Stack(Label("Cloudlet 2.0", 18, true),
            Label("C# · WinUI 3 · Windows 11"),
            Hint("rclone — Copyright © The rclone authors · MIT License"),
            SimpleButton("RcloneLicense", "查看 rclone 开源许可", () => NativeIntegration.OpenUrl("https://github.com/rclone/rclone/blob/master/COPYING")),
            Hint("WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos"),
            SimpleButton("WinFspLicense", "WinFsp 项目与许可", () => NativeIntegration.OpenUrl("https://github.com/winfsp/winfsp")))));
        return page;
    }

    private Grid SettingsPathRow(TextBox input, string name, Func<Task> action)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(input);
        var button = ActionButton(name, "浏览…", action, "\uE8B7");
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }
}
