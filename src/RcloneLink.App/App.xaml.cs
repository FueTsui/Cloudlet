using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public partial class App : Application
{
    private MainWindow? _window;
    private AppState? _state;
    private SettingsStore? _store;
    private MountManager? _mounts;
    private Task? _autoMountTask;
    private TrayIcon? _tray;
    private Mutex? _instance;
    private bool _ownsMutex, _exiting, _allowWindowClose;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _autoMount = new();
    private string _pipeName = "";
    internal string? TrayIconPath => _tray?.CurrentIconPath;
    internal nint TrayIconHandle => _tray?.NativeIconHandle ?? 0;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneLink", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} {e.Exception.GetType().Name}\n{e.Exception.StackTrace}\n");
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = Environment.GetCommandLineArgs();
        var silent = command.Contains("--minimized");
        var smokeIndex = Array.IndexOf(command, "--ui-smoke");
        var smokeDirectory = smokeIndex >= 0 && smokeIndex + 1 < command.Length ? Path.GetFullPath(command[smokeIndex + 1]) : null;
        var dataIndex = Array.IndexOf(command, "--data-dir");
        var dataDirectory = dataIndex >= 0 && dataIndex + 1 < command.Length ? Path.GetFullPath(command[dataIndex + 1]) : null;
        if (smokeDirectory != null) dataDirectory ??= Path.Combine(smokeDirectory, "isolated-state");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + (dataDirectory ?? "default"))))[..20];
        _pipeName = "RcloneLink-v2-" + identity;
        _instance = new Mutex(true, @"Local\" + _pipeName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try { if (!silent) { using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out); await pipe.ConnectAsync(2500); await pipe.WriteAsync("show"u8.ToArray()); } }
            catch { }
            Exit(); return;
        }
        try
        {
            _store = new SettingsStore(dataDirectory);
            _state = _store.Load();
            if (smokeDirectory != null)
            {
                _state.Settings.ConfigPath = Path.Combine(dataDirectory!, "smoke-rclone.conf");
                Directory.CreateDirectory(dataDirectory!);
                await File.WriteAllTextAsync(_state.Settings.ConfigPath, "[local-test]\ntype = local\n");
                _state.Settings.AutoMount = false;
                _state.Settings.StartWithWindows = false;
                _state.Settings.TrayVisible = false;
                _state.Settings.CheckDependencyUpdates = false;
                _state.Settings.AutoUpdateRclone = false;
            }
            _store.Save(_state);
            var service = new RcloneService(_state.Settings);
            _mounts = new MountManager(service);
            _window = new MainWindow(_state, _store, service, _mounts);
            _window.SettingsChanged += () => _tray?.SetVisible(_state.Settings.TrayVisible);
            _window.AppWindow.Closing += (sender, e) =>
            {
                if (_allowWindowClose) return;
                e.Cancel = true;
                if (_exiting) return;
                if (_state.Settings.CloseToTray && smokeDirectory == null) _window.AppWindow.Hide();
                else _ = ExitAsync();
            };
            _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_window), ShowWindow, () => _ = ExitAsync(), () => _window.DispatcherQueue.TryEnqueue(_window.RefreshBrandIcons));
            _tray.SetVisible(_state.Settings.TrayVisible);
            if (!silent || smokeDirectory != null) _window.Activate();
            _ = ListenAsync();
            await _window.InitializeAsync();
            if (_exiting) return;
            if (smokeDirectory != null)
            {
                Directory.CreateDirectory(smokeDirectory);
                await Task.Delay(1200);
                await _window.RunVisualSmokeAsync(smokeDirectory);
                await ExitAsync();
                return;
            }
            if (_state.Settings.AutoMount)
            {
                _autoMountTask = _mounts.StartAutomaticAsync(_state.Mounts, _autoMount.Token);
                try { await _autoMountTask; }
                catch (OperationCanceledException) when (_autoMount.IsCancellationRequested) { return; }
            }
            if (command.Contains("--startup-smoke") && dataDirectory != null)
            {
                await Task.Delay(1500);
                await File.WriteAllTextAsync(Path.Combine(dataDirectory, "startup-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    silent, visible = _window.AppWindow.IsVisible,
                    mounts = _mounts.GetStates().Select(s => new { s.Id, status = s.Status.ToString(), s.ProcessId })
                }));
                await ExitAsync();
                return;
            }
            _window.StartUpdateChecks();
        }
        catch (Exception error)
        {
            if (smokeDirectory != null)
            {
                Directory.CreateDirectory(smokeDirectory);
                await File.WriteAllTextAsync(Path.Combine(smokeDirectory, "startup-error.txt"), error.ToString());
            }
            else if (silent)
            {
                var logDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RcloneLink", "v2");
                Directory.CreateDirectory(logDirectory);
                await File.WriteAllTextAsync(Path.Combine(logDirectory, "startup-error.txt"), $"{DateTimeOffset.Now:O} {error.GetType().Name}：后台启动未完成，请手动打开 Cloudlet 检查设置。");
            }
            else
            {
                _window ??= new MainWindow(new AppState(), new SettingsStore(dataDirectory), new RcloneService(new AppSettings()), new MountManager(new RcloneService(new AppSettings())));
                _window.Activate();
                await Task.Delay(300);
                var dialog = new ContentDialog { XamlRoot = _window.Content.XamlRoot, Title = "无法启动 Cloudlet", Content = error.Message, CloseButtonText = "关闭" };
                await dialog.ShowAsync();
            }
            await ExitAsync();
        }
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.AppWindow.Show();
        _window.Activate();
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                var bytes = new byte[4];
                await pipe.ReadExactlyAsync(bytes, _lifetime.Token);
                if (Encoding.UTF8.GetString(bytes) == "show") _window?.DispatcherQueue.TryEnqueue(ShowWindow);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _autoMount.Cancel();
        _window?.SetExitPending(true);
        var stoppingMounts = false;
        try
        {
            if (_autoMountTask != null) { try { await _autoMountTask; } catch (OperationCanceledException) { } }
            if (_window != null) await _window.ShutdownAsync();
            stoppingMounts = true;
            if (_mounts != null) await _mounts.StopAllAsync();
        }
        catch (Exception error)
        {
            _exiting = false;
            _window?.SetExitPending(false);
            ShowWindow();
            if (_window != null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = _window.Content.XamlRoot,
                    RequestedTheme = (_window.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
                    Title = stoppingMounts ? "Cloudlet 尚未安全卸载" : "Cloudlet 无法完成退出",
                    Content = error.Message + (stoppingMounts ? "\n请处理挂载状态后重试退出。" : "\n退出时清理未完成，请返回 Cloudlet 后重试。"),
                    CloseButtonText = "返回 Cloudlet"
                };
                await dialog.ShowAsync();
            }
            return;
        }
        _lifetime.Cancel();
        try
        {
            if (_mounts != null) await _mounts.DisposeAsync();
            if (_state != null && _store != null) _store.Save(_state);
        }
        finally
        {
            _tray?.Dispose();
            if (_ownsMutex) { _instance?.ReleaseMutex(); _ownsMutex = false; }
            _instance?.Dispose();
            _allowWindowClose = true;
            _window?.Close();
            Exit();
        }
    }
}
