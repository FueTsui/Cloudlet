using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using RcloneLink.Core;
using Windows.Graphics;

namespace RcloneLink.App;

public sealed partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly SettingsStore _store;
    private readonly RcloneService _service;
    private readonly MountManager _mounts;
    private readonly NativeWindowIcons _windowIcons;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NavigationView _navigation;
    private readonly Grid _root;
    private readonly ContentControl _pageHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly ProgressRing _busyRing = new() { Width = 18, Height = 18, IsActive = false };
    private readonly TextBlock _busyText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly Queue<string> _logLines = new();
    private readonly TextBox _applicationLog = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220, MaxHeight = 420 };
    private IReadOnlyList<string> _remotes = Array.Empty<string>();
    private string _currentPage = "overview";
    private int _busyCount;
    private bool _started;
    public event Action? SettingsChanged;
    internal string WindowIconPath => _windowIcons.SmallIconPath;
    internal string TaskbarIconPath => _windowIcons.LargeIconPath;
    internal nint WindowIconHandle => _windowIcons.SmallIconHandle;
    internal nint TaskbarIconHandle => _windowIcons.LargeIconHandle;

    public MainWindow(AppState state, SettingsStore store, RcloneService service, MountManager mounts)
    {
        _state = state;
        _store = store;
        _service = service;
        _mounts = mounts;
        Title = "Cloudlet";
        _windowIcons = new NativeWindowIcons(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Closed += (_, _) => _windowIcons.Dispose();
        SystemBackdrop = new MicaBackdrop();
        var dpiScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (dpiScale < 1) dpiScale = 1;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var windowWidth = Math.Min((int)Math.Round(1120 * dpiScale), Math.Max(640, workArea.Width - 48));
        var windowHeight = Math.Min((int)Math.Round(780 * dpiScale), Math.Max(480, workArea.Height - 48));
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - windowWidth) / 2, workArea.Y + (workArea.Height - windowHeight) / 2, windowWidth, windowHeight));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        _root = new Grid();
        _navigation = new NavigationView
        {
            Name = "MainNavigation",
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = true,
            OpenPaneLength = 220,
            CompactPaneLength = 56,
            ExpandedModeThresholdWidth = 900,
            CompactModeThresholdWidth = 720,
            IsPaneOpen = true,
            PaneTitle = "Cloudlet",
            AlwaysShowHeader = false
        };
        _navigation.MenuItems.Add(NavigationItem("overview", "概览", "\uE80F"));
        _navigation.MenuItems.Add(NavigationItem("remotes", "远程连接", "\uE774"));
        _navigation.MenuItems.Add(NavigationItem("mounts", "磁盘挂载", "\uEDA2"));
        _navigation.MenuItems.Add(NavigationItem("files", "文件浏览", "\uE8B7"));
        _navigation.MenuItems.Add(NavigationItem("transfers", "传输任务", "\uE8AB"));
        _navigation.PaneFooter = new TextBlock
        {
            Text = "Cloudlet 2.0",
            FontSize = 11,
            Opacity = .65,
            Margin = new Thickness(20, 12, 12, 20),
            TextWrapping = TextWrapping.Wrap
        };
        var contentGrid = new Grid { Margin = new Thickness(28, 22, 32, 24) };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var activity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        activity.Children.Add(_busyRing);
        activity.Children.Add(_busyText);
        contentGrid.Children.Add(activity);
        Grid.SetRow(_pageHost, 1);
        contentGrid.Children.Add(_pageHost);
        _notice.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(_notice, 2);
        contentGrid.Children.Add(_notice);
        _navigation.Content = contentGrid;
        _root.Children.Add(_navigation);
        Content = _root;
        _root.ActualThemeChanged += (_, _) => RefreshBrandIcons();
        ApplyTheme();
        _navigation.SelectionChanged += (_, args) => ShowPage(args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "overview");

        _pages["overview"] = BuildOverviewPage();
        _pages["remotes"] = BuildRemotesPage();
        _pages["providers"] = BuildProvidersPage();
        _pages["mounts"] = BuildMountsPage();
        _pages["files"] = BuildFilesPage();
        _pages["transfers"] = BuildTransfersPage();
        _pages["settings"] = BuildSettingsPage();
        _navigation.SelectedItem = _navigation.MenuItems[0];
        ShowPage("overview");
        _mounts.StatusChanged += OnMountStatusChanged;
        _mounts.OutputReceived += OnMountOutput;
        _root.Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await RunUiAsync(RefreshHealthAsync, "检查运行环境");
            await RunUiAsync(RefreshRemotesAsync, "读取远程连接");
            RefreshMountRows();
        };
    }

    private NavigationViewItem NavigationItem(string key, string text, string glyph)
    {
        var item = new NavigationViewItem { Content = text, Tag = key, Name = "Navigation" + key, Icon = new FontIcon { Glyph = glyph } };
        AutomationProperties.SetName(item, text);
        return item;
    }

    public void Navigate(string page)
    {
        if (!_pages.ContainsKey(page)) return;
        if (page == "settings") _navigation.SelectedItem = _navigation.SettingsItem;
        else _navigation.SelectedItem = _navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, page == "providers" ? "remotes" : page));
        ShowPage(page);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        _currentPage = key;
        _pageHost.Content = page;
        if (key == "mounts") RefreshMountRows();
        if (key == "overview") RefreshOverviewCounts();
    }

    private FrameworkElement Page(string title, string description, out StackPanel body)
    {
        var outer = new StackPanel { Spacing = 24, MaxWidth = 1250, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heading = new StackPanel { Spacing = 7, Margin = new Thickness(0, 0, 0, 2) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = description, FontSize = 14, Opacity = .7, TextWrapping = TextWrapping.Wrap });
        outer.Children.Add(heading);
        body = new StackPanel { Spacing = 16 };
        outer.Children.Add(body);
        return new ScrollViewer { Content = outer, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 12) };
    }

    private Border Card(UIElement child, Thickness? padding = null)
    {
        // Native ThemeResource expressions remain attached to the visual tree even
        // if a managed wrapper is collected; detached pages update when shown.
        var card = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\" BorderThickness=\"1\" CornerRadius=\"8\" HorizontalAlignment=\"Stretch\" />");
        card.Tag = "RcloneLinkCard";
        card.Padding = padding ?? new Thickness(20);
        card.Child = child;
        return card;
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static TextBlock Label(string text, double size = 14, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap };

    private static TextBox TextInput(string name, string header, string value = "", string placeholder = "")
    {
        var input = new TextBox { Name = name, Header = header, Text = value, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 160 };
        AutomationProperties.SetName(input, header);
        return input;
    }

    private static ComboBox SelectInput(string name, string header, IEnumerable<string> options, string? selected = null)
    {
        var combo = new ComboBox { Name = name, Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 160 };
        foreach (var option in options) combo.Items.Add(option);
        if (selected != null && combo.Items.Contains(selected)) combo.SelectedItem = selected;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        AutomationProperties.SetName(combo, header);
        return combo;
    }

    private static ToggleSwitch Toggle(string name, string header, bool value)
    {
        var control = new ToggleSwitch { Name = name, Header = header, IsOn = value, OnContent = "已开启", OffContent = "已关闭" };
        AutomationProperties.SetName(control, header);
        return control;
    }

    private Button ActionButton(string name, string text, Func<Task> action, string? glyph = null, bool accent = false)
    {
        var button = new Button { Name = name, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        button.Content = glyph == null ? text : Horizontal(new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text });
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) =>
        {
            if (_exitPending) return;
            button.IsEnabled = false;
            try { await RunUiAsync(action, text); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Button SimpleButton(string name, string text, Action action, string? glyph = null, bool accent = false) =>
        ActionButton(name, text, () => { action(); return Task.CompletedTask; }, glyph, accent);

    private async Task RunUiAsync(Func<Task> action, string activity)
    {
        _busyCount++;
        _busyRing.IsActive = true;
        _busyText.Text = activity + "…";
        try { await action(); }
        catch (OperationCanceledException) { Notify("操作已取消。", InfoBarSeverity.Informational); }
        catch (Exception ex) { Notify(ex.Message, InfoBarSeverity.Error); AppendLog("错误：" + ex.Message); }
        finally
        {
            _busyCount--;
            _busyRing.IsActive = _busyCount > 0;
            if (_busyCount == 0) _busyText.Text = "";
        }
    }

    private void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        _notice.Severity = severity;
        _notice.Title = severity switch { InfoBarSeverity.Error => "操作未完成", InfoBarSeverity.Warning => "需要留意", _ => "" };
        var safeMessage = CleanLog(message);
        _notice.Message = safeMessage.Length > 900 ? safeMessage[..900] + "…（详细信息见日志）" : safeMessage;
        _notice.IsOpen = true;
    }

    private static string CleanLog(string line)
    {
        line = Regex.Replace(line, @"(?i)(password|passwd|token|authorization|secret)(\s*[:=]\s*)[^\s,;]+", "$1$2••••");
        return Regex.Replace(line, @"(https?://)[^\s/@]+:[^\s/@]+@", "$1••••@");
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {CleanLog(line)}");
        while (_logLines.Count > 250) _logLines.Dequeue();
        _applicationLog.Text = string.Join(Environment.NewLine, _logLines);
    }

    private void OnMountOutput(object? sender, string line) => DispatcherQueue.TryEnqueue(() => AppendLog(line));

    private void OnMountStatusChanged(object? sender, MountState status) => DispatcherQueue.TryEnqueue(() =>
    {
        AppendLog($"挂载 {(_state.Mounts.FirstOrDefault(m => m.Id == status.Id)?.Name ?? status.Id)}：{status.Message}");
        RefreshMountRows();
        RefreshOverviewCounts();
    });

    private void SaveState()
    {
        _store.Save(_state);
        SettingsChanged?.Invoke();
    }

    private void ApplyTheme()
    {
        _root.RequestedTheme = _state.Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = _state.Settings.Theme switch { "Light" => TitleBarTheme.Light, "Dark" => TitleBarTheme.Dark, _ => TitleBarTheme.UseDefaultAppMode };
        RefreshBrandIcons();
    }

    internal void RefreshBrandIcons() => _windowIcons.Update(_root.ActualTheme == ElementTheme.Dark, NativeIntegration.IsSystemTaskbarDark());

    private async Task<bool> ConfirmAsync(string title, string message, string confirm = "继续")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = title, Content = Label(message),
            PrimaryButtonText = confirm, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = _root.RequestedTheme
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static void RequireSuccess(CommandResult result, string success)
    {
        if (result.Cancelled) throw new OperationCanceledException();
        if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? $"{success}失败，退出码 {result.ExitCode}。" : result.Error);
    }

    private static string FormatBytes(long value)
    {
        if (value < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = value;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.##} {units[index]}";
    }

    public async Task ShutdownAsync()
    {
        _transferCancellation?.Cancel();
        if (_transferTask != null) { try { await _transferTask; } catch { } }
        await CancelProviderWizardAsync(false);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
