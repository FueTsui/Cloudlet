using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    // Runs only under --ui-smoke with App-provided isolated state/configuration.
    // These are real WinUI renders; this does not claim desktop pointer/input testing.
    internal async Task RunVisualSmokeAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var report = new List<object>();
        var providerScenes = new List<object>();
        var brandingThemes = new List<object>();
        var originalTheme = _state.Settings.Theme;
        var originalBackground = _root.Background;
        await OpenProviderSetupAsync();
        if (_providerCatalog.Count == 0 || _providerError.IsOpen) throw new InvalidOperationException("提供商目录未加载。");
        if ((_navigation.PaneFooter as TextBlock)?.Text.Contains("基于 rclone", StringComparison.Ordinal) != false)
            throw new InvalidOperationException("侧边栏描述未移除。");
        if (Title != "Cloudlet" || _navigation.PaneTitle != "Cloudlet" || (_navigation.PaneFooter as TextBlock)?.Text != "Cloudlet 2.0")
            throw new InvalidOperationException("Cloudlet 窗口名称未完整更新。");
        var startupCommand = $"\"{Environment.ProcessPath}\" --minimized";
        if (!NativeIntegration.IsProductStartupCommand(startupCommand) || NativeIntegration.IsProductStartupCommand(startupCommand + " --unexpected"))
            throw new InvalidOperationException("Cloudlet 启动项身份校验失败。");
        foreach (var theme in new[] { "Light", "Dark" })
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            _state.Settings.Theme = theme;
            ApplyTheme();
            // System backdrop lives outside the XAML visual tree captured by RenderTargetBitmap.
            _root.Background = new SolidColorBrush(theme == "Dark" ? ColorHelper.FromArgb(255, 32, 32, 32) : ColorHelper.FromArgb(255, 243, 243, 243));
            foreach (var page in new[] { "overview", "remotes", "providers", "mounts", "files", "transfers", "settings" })
            {
                Navigate(page);
                await Task.Delay(350);
                _root.UpdateLayout();
                var name = $"{theme.ToLowerInvariant()}-{page}.png";
                var capture = await CaptureAsync(Path.Combine(outputDirectory, name));
                var nodes = Descendants(_pageHost).ToList();
                var cardsValidated = 0;
                foreach (var card in nodes.OfType<Border>().Where(x => Equals(x.Tag, "RcloneLinkCard")))
                {
                    if (card.Background is not SolidColorBrush brush) throw new InvalidOperationException("无法验证卡片主题背景。");
                    var color = brush.Color;
                    var background = theme == "Dark" ? 32 : 243;
                    var effective = (color.R + color.G + color.B) / 3.0 * color.A / 255.0 + background * (1 - color.A / 255.0);
                    if (theme == "Dark" && effective > 128 || theme == "Light" && effective < 180)
                        throw new InvalidOperationException($"主题对比错误：{theme}/{page}，卡片背景亮度 {effective:0}。");
                    cardsValidated++;
                }
                if (cardsValidated == 0) throw new InvalidOperationException($"缺少可验证卡片：{theme}/{page}");
                var content = nodes.OfType<TextBlock>().Where(x => x.ActualWidth > 0 && x.ActualHeight > 0).Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
                if (capture.Width < 600 || capture.Height < 400 || content.Length == 0) throw new InvalidOperationException($"页面渲染不完整：{theme}/{page}");
                string? bottomScreenshot = null;
                if (_pages[page] is ScrollViewer scroller && scroller.ScrollableHeight > 0)
                {
                    scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
                    await Task.Delay(150);
                    bottomScreenshot = $"{theme.ToLowerInvariant()}-{page}-bottom.png";
                    await CaptureAsync(Path.Combine(outputDirectory, bottomScreenshot));
                    scroller.ChangeView(null, 0, null, true);
                    await Task.Delay(100);
                }
                report.Add(new
                {
                    theme, page, screenshot = name, bottomScreenshot, cardsValidated, capture.Width, capture.Height,
                    visibleText = content,
                    controls = nodes.OfType<Control>().Where(x => x.ActualWidth > 0).Select(x => new { kind = x.GetType().Name, x.Name, accessibilityName = AutomationProperties.GetName(x) }).ToArray(),
                    hostWidth = _pageHost.ActualWidth,
                    pageWidth = (_pageHost.Content as FrameworkElement)?.ActualWidth,
                    requiresScrolling = nodes.OfType<ScrollViewer>().Any(x => x.ScrollableHeight > 0)
                });
            }
            foreach (var provider in new[] { "sftp", "s3", "drive", "onedrive" })
            {
                Navigate("providers");
                SelectProvider(_providerCatalog.First(p => p.Name == provider));
                _providerRemoteName.Text = "我的" + provider;
                if (provider == "sftp" && _providerFields.First(f => f.Option.Name == "pass").Input is not PasswordBox)
                    throw new InvalidOperationException("SFTP 密码未使用密码输入控件。");
                if (provider == "s3")
                {
                    var branch = (ComboBox)_providerFields.First(f => f.Option.Name == "provider").Input;
                    branch.SelectedItem = branch.Items.OfType<ComboBoxItem>().First(i => i.Tag?.ToString() == "Other");
                    if (!_providerFields.Any(f => f.Option.Name == "endpoint")) throw new InvalidOperationException("S3 服务商条件字段未更新。");
                    var selectedField = _providerFields.First(f => f.Option.Name == "provider");
                    if (selectedField.Read() != "Other") throw new InvalidOperationException("S3 服务商字段未保留实际参数值。");
                    var selectedCombo = (ComboBox)selectedField.Input;
                    var displayText = selectedCombo.Text;
                    selectedCombo.Text = "custom-provider";
                    if (selectedField.Read() != "custom-provider") throw new InvalidOperationException("下拉字段未保留自定义输入。");
                    selectedCombo.Text = "";
                    if (selectedField.Read() != "") throw new InvalidOperationException("清空下拉输入后残留旧值。");
                    selectedCombo.Text = displayText;
                }
                if (_pages["providers"] is ScrollViewer formScroll) formScroll.ChangeView(null, 0, null, true);
                await Task.Delay(250);
                var name = $"{theme.ToLowerInvariant()}-provider-{provider}.png";
                await CaptureAsync(Path.Combine(outputDirectory, name));
                providerScenes.Add(new { theme, provider, stage = "form", screenshot = name, fields = _providerFields.Select(f => f.Option.Name).ToArray() });
            }
            // Stop at the real OAuth question: no personal account or browser authorization is invoked.
            await ContinueProviderWizardAsync();
            if (_providerError.IsOpen || !IsBrowserAuthorizationStep(_providerSession?.CurrentStep.Option))
                throw new InvalidOperationException("OneDrive 未进入浏览器授权确认步骤。");
            await Task.Delay(250);
            var oauthScreenshot = $"{theme.ToLowerInvariant()}-provider-authorization.png";
            await CaptureAsync(Path.Combine(outputDirectory, oauthScreenshot));
            providerScenes.Add(new { theme, provider = "onedrive", stage = "authorization-prompt", screenshot = oauthScreenshot });
            await CancelProviderWizardAsync(true);
            var expectedWindowIcon = theme == "Dark" ? "icon-white.ico" : "icon-black.ico";
            var expectedSystemIcon = NativeIntegration.IsSystemTaskbarDark() ? "icon-white.ico" : "icon-black.ico";
            var application = (App)Application.Current;
            if (Path.GetFileName(WindowIconPath) != expectedWindowIcon || WindowIconHandle == 0 || !_windowIcons.NativeHandlesMatch)
                throw new InvalidOperationException("窗口主题图标未正确加载。");
            if (Path.GetFileName(TaskbarIconPath) != expectedSystemIcon || TaskbarIconHandle == 0 ||
                Path.GetFileName(application.TrayIconPath) != expectedSystemIcon || application.TrayIconHandle == 0)
                throw new InvalidOperationException("任务栏或托盘图标未正确加载。");
            brandingThemes.Add(new { theme, title = Title, windowIcon = Path.GetFileName(WindowIconPath), taskbarIcon = Path.GetFileName(TaskbarIconPath), trayIcon = Path.GetFileName(application.TrayIconPath), nativeHandlesVerified = true });
        }
        var providerFlow = await VerifyProviderPageFlowAsync();
        // Verify the compact navigation breakpoint has a usable viewport.
        AppWindow.Resize(new SizeInt32(1000, 720));
        var compactScenes = new List<object>();
        foreach (var page in new[] { "overview", "remotes", "providers", "mounts", "files", "transfers", "settings" })
        {
            Navigate(page);
            await Task.Delay(350);
            var fileName = "compact-" + page + ".png";
            await CaptureAsync(Path.Combine(outputDirectory, fileName));
            compactScenes.Add(new { page, screenshot = fileName, width = _pageHost.ActualWidth, navigationMode = _navigation.DisplayMode.ToString() });
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "visual-smoke.json"), JsonSerializer.Serialize(new
        {
            verifiedAt = DateTimeOffset.Now,
            kind = "WinUI in-process visual rendering",
            limitations = "No desktop pointer, keyboard, system picker, tray menu or installer interaction was performed by this test.",
            passed = report.Count == 14 && providerScenes.Count == 10,
            pages = report,
            compactScenes,
            providerScenes,
            providerFlow,
            product = "Cloudlet",
            brandingThemes,
            compactHostWidth = _pageHost.ActualWidth
        }, new JsonSerializerOptions { WriteIndented = true }));
        _state.Settings.Theme = originalTheme;
        _root.Background = originalBackground;
        ApplyTheme();
    }

    private async Task<object> VerifyProviderPageFlowAsync()
    {
        static string ConfigHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var before = ConfigHash(_state.Settings.ConfigPath);
        await OpenProviderSetupAsync();
        _providerSearch.Text = "sftp";
        await Task.Delay(150); // WinUI TextChanged is dispatched after the Text property update.
        if (_providerList.Items.Count != 1 || (_providerList.Items[0] as ListViewItem)?.Tag is not RcloneLink.Core.RcloneProvider { Name: "sftp" })
            throw new InvalidOperationException("提供商搜索未筛选到 SFTP。");
        _providerSearch.Text = "";
        await Task.Delay(150);
        SelectProvider(_providerCatalog.First(p => p.Name == "local"));
        var rejectedEmptyName = false;
        try { await ContinueProviderWizardAsync(); }
        catch (ArgumentException) { rejectedEmptyName = true; }
        if (!rejectedEmptyName) throw new InvalidOperationException("空连接名称未被阻止。");
        _providerRemoteName.Text = "ui-local-created";
        await ContinueProviderWizardAsync();
        if (_providerSession?.CurrentStep.Complete != true || _providerError.IsOpen) throw new InvalidOperationException("页面无法完成本地提供商设置。");
        if (ConfigHash(_state.Settings.ConfigPath) != before) throw new InvalidOperationException("向导在保存前修改了实际配置。");
        await ContinueProviderWizardAsync();
        if (_providerSession != null || !_remotes.Contains("ui-local-created") || _currentPage != "remotes")
            throw new InvalidOperationException("页面保存后未刷新远程连接。");
        var saved = ConfigHash(_state.Settings.ConfigPath);
        if (saved == before) throw new InvalidOperationException("页面未保存新连接。");
        await OpenProviderSetupAsync();
        SelectProvider(_providerCatalog.First(p => p.Name == "local"));
        _providerRemoteName.Text = "ui-local-created";
        await ContinueProviderWizardAsync();
        if (!_providerError.IsOpen || _providerSession != null || ConfigHash(_state.Settings.ConfigPath) != saved)
            throw new InvalidOperationException("页面同名连接保护失效。");
        await CancelProviderWizardAsync(true);
        await OpenProviderSetupAsync();
        SelectProvider(_providerCatalog.First(p => p.Name == "local"));
        _providerRemoteName.Text = "ui-cancelled";
        await ContinueProviderWizardAsync();
        if (_providerSession?.CurrentStep.Complete != true) throw new InvalidOperationException("取消场景未进入待保存状态。");
        await PreviousProviderStepAsync();
        if (_providerRemoteName.Text != "ui-cancelled" || _providerSession != null || _providerStage != "form")
            throw new InvalidOperationException("返回表单未保留填写内容。");
        await ContinueProviderWizardAsync();
        await CancelProviderWizardAsync(true);
        if (ConfigHash(_state.Settings.ConfigPath) != saved || ProviderWizardActive || (await _service.ListRemotesAsync()).Contains("ui-cancelled"))
            throw new InvalidOperationException("取消设置未保持原配置。");
        return new { passed = true, providerCount = _providerCatalog.Count, assertions = new[] { "catalog search", "required name", "password control", "conditional S3 fields", "OAuth prompt without authorization", "draft isolation", "save and refresh", "duplicate protection", "back keeps form", "cancel preserves configuration" } };
    }

    private async Task<(int Width, int Height)> CaptureAsync(string destination)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(_root);
        var pixels = (await target.GetPixelsAsync()).ToArray();
        using var output = File.Open(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var randomAccess = output.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccess);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        return (target.PixelWidth, target.PixelHeight);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
