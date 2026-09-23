using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    internal static string ProductLabel => "Cloudlet " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    private readonly DependencyUpdates _updates = new();
    private readonly TextBlock _rcloneUpdate = Hint("尚未在线检查。");
    private readonly TextBlock _winFspUpdate = Hint("尚未在线检查。");
    private readonly TextBlock _startupStatus = Hint("");
    private readonly TextBlock _settingsStartupStatus = Hint("");
    private bool _updating;
    private Task? _updateTask;
    private Task? _installerTask;
    private DispatcherTimer? _updateTimer;
    private DateTimeOffset _nextUpdateCheck;
    private bool _updateDeferred;
    private CancellationTokenSource _updateLifetime = new();

    internal async Task InitializeAsync()
    {
        if (_started) return;
        _started = true;
        await RunUiAsync(RefreshHealthAsync, "检查运行环境");
        await RunUiAsync(RefreshRemotesAsync, "读取远程连接");
        RefreshMountRows();
    }

    internal void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += (_, _) => QueueUpdateCheck();
        _updateTimer.Start();
        QueueUpdateCheck();
    }

    private void QueueUpdateCheck()
    {
        if (_state.Settings.CheckDependencyUpdates && !_exitPending && _installerTask?.IsCompleted != false && _updateTask?.IsCompleted != false &&
            (DateTimeOffset.Now >= _nextUpdateCheck || _updateDeferred && !EngineBusy && _busyCount == 0))
            _updateTask = CheckUpdatesAsync(_state.Settings.AutoUpdateRclone);
    }

    private bool EngineBusy => _activeTransfer != null || ProviderWizardActive ||
        _mounts.GetStates().Any(s => s.Status is MountStatus.Starting or MountStatus.Mounted or MountStatus.Stopping);

    private Task ManualUpdateCheckAsync(bool install)
    {
        if (_updateTask?.IsCompleted == false) return _updateTask;
        return _updateTask = CheckUpdatesAsync(install, true);
    }

    private async Task CheckUpdatesAsync(bool installRclone, bool manual = false)
    {
        if (_updating || _exitPending) return;
        _updating = true;
        _updateDeferred = false;
        _nextUpdateCheck = DateTimeOffset.Now.AddHours(6);
        try
        {
            _rcloneUpdate.Text = "正在查询官方稳定版…";
            try
            {
                var latest = await _updates.CheckRcloneAsync(_updateLifetime.Token);
                var current = DependencyUpdates.ParseVersion(await _service.GetVersionAsync(_updateLifetime.Token));
                _rcloneUpdate.Text = current >= latest.Version ? "已是最新稳定版" : $"可更新至 {latest.Version.ToString(3)}";
                if (current < latest.Version && installRclone && (manual || _state.Settings.AutoUpdateRclone && _state.Settings.CheckDependencyUpdates))
                {
                    if (EngineBusy || _busyCount > (manual ? 1 : 0))
                    {
                        _updateDeferred = true;
                        _rcloneUpdate.Text += " · 正在使用引擎，空闲后自动重试";
                    }
                    else
                    {
                        _navigation.IsEnabled = false;
                        _rcloneUpdate.Text = "正在下载并校验更新…";
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromMinutes(5));
                        var path = await _updates.StageRcloneAsync(_service, latest, Path.Combine(Path.GetDirectoryName(_store.StatePath)!, "engines"), timeout.Token);
                        var previous = _state.Settings.RclonePath;
                        _state.Settings.RclonePath = path;
                        try { SaveState(); }
                        catch { _state.Settings.RclonePath = previous; throw; }
                        _rcloneUpdate.Text = "已更新至 " + latest.Version.ToString(3) + " · 旧引擎已保留";
                        _versionValue.Text = "rclone v" + latest.Version.ToString(3);
                        _settingsRclonePath?.SetValue(TextBox.TextProperty, path);
                    }
                }
            }
            catch (Exception ex) { _rcloneUpdate.Text = "检查 / 更新未完成：" + CleanLog(ex.Message); }
            try
            {
                _winFspUpdate.Text = "正在查询官方稳定版…";
                var latest = await _updates.CheckWinFspAsync(_updateLifetime.Token);
                var current = NativeIntegration.GetWinFspVersion();
                _winFspUpdate.Text = current == null ? $"可安装 {latest.Version.ToString(3)}" : current >= latest.Version ? "已是最新稳定版" : $"可更新至 {latest.Version.ToString(3)}";
            }
            catch (Exception ex) { _winFspUpdate.Text = "在线检查未完成：" + CleanLog(ex.Message); }
        }
        finally { _updating = false; _navigation.IsEnabled = !_exitPending; }
    }

    private Task UpdateWinFspAsync() => _installerTask?.IsCompleted == false ? _installerTask : _installerTask = InstallLatestWinFspAsync();

    private async Task InstallLatestWinFspAsync()
    {
        if (_updateTask?.IsCompleted == false) await _updateTask;
        if (EngineBusy) throw new InvalidOperationException("请先停止挂载、传输和连接配置，再安装 WinFsp。");
        _navigation.IsEnabled = false;
        try
        {
            var latest = await _updates.CheckWinFspAsync(_updateLifetime.Token);
            var path = await _updates.DownloadWinFspAsync(latest, Path.Combine(Path.GetDirectoryName(_store.StatePath)!, "downloads"), _updateLifetime.Token);
            var reboot = await NativeIntegration.InstallWinFspAsync(path);
            await RefreshHealthAsync();
            Notify(reboot ? "WinFsp 已安装，需要重启 Windows 后才能使用。" : "WinFsp 安装已完成。", reboot ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally { _navigation.IsEnabled = !_exitPending; }
    }

    private void RefreshStartupStatus()
    {
        var selected = _state.Mounts.Count(m => m.AutoMount);
        var message = NativeIntegration.GetStartupStatus() + "\n" +
            (!_state.Settings.AutoMount ? "自动挂载未开启。" : selected == 0 ? "自动挂载已开启，但没有勾选自动挂载的配置。" : $"已选择 {selected} 个自动挂载；启动失败时会重试，结果见挂载状态。") +
            (NativeIntegration.IsWinFspInstalled() ? "" : "\n尚未安装 WinFsp，磁盘挂载不可用。");
        _startupStatus.Text = _settingsStartupStatus.Text = message;
    }
}
