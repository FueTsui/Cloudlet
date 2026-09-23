namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private bool _exitPending;
    internal void SetExitPending(bool pending)
    {
        _exitPending = pending;
        _navigation.IsEnabled = !pending;
        _busyRing.IsActive = pending;
        _busyText.Text = pending ? "正在安全退出：等待传输停止和缓存写入完成…" : "";
    }
}
