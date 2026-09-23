using System.Runtime.InteropServices;

namespace RcloneLink.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Action? _appearanceChanged;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _visible;
    internal string CurrentIconPath { get; private set; } = "";
    internal nint NativeIconHandle => _icon;
    private const uint TrayMessage = 0x8000 + 77;
    public TrayIcon(nint window, Action show, Action exit, Action? appearanceChanged = null)
    {
        _window = window; _show = show; _exit = exit;
        _appearanceChanged = appearanceChanged;
        _callback = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        RefreshIcon();
        SetWindowSubclass(_window, _callback, 77, 0);
    }
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        var data = Data();
        Shell_NotifyIcon(visible ? 0u : 2u, ref data);
        _visible = visible;
    }
    private NotifyIconData Data() => new() { cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = _window, uID = 77, uFlags = 1 | 2 | 4, uCallbackMessage = TrayMessage, hIcon = _icon, szTip = "Cloudlet", szInfo = "", szInfoTitle = "" };
    private void RefreshIcon()
    {
        var path = NativeIntegration.GetThemeIconPath(NativeIntegration.IsSystemTaskbarDark());
        if (path == CurrentIconPath && _icon != 0) return;
        var replacement = LoadImage(0, path, 1, 32, 32, 0x10);
        if (replacement == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法加载 Cloudlet 托盘图标。");
        var previous = _icon;
        _icon = replacement;
        CurrentIconPath = path;
        if (_visible) { var data = Data(); Shell_NotifyIcon(1, ref data); }
        if (previous != 0) DestroyIcon(previous);
    }
    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == _taskbarCreated && _visible) { var data = Data(); Shell_NotifyIcon(0, ref data); }
        if (message is 0x001A or 0x031A or 0x02E0 || message == _taskbarCreated)
        {
            // Never propagate managed exceptions through a native window procedure.
            try { RefreshIcon(); _appearanceChanged?.Invoke(); } catch { }
        }
        if (message == TrayMessage)
        {
            var input = (uint)((long)lParam & 0xffff);
            if (input is 0x0203 or 0x0202) _show();
            if (input == 0x0205)
            {
                var menu = CreatePopupMenu();
                AppendMenu(menu, 0, 1, "打开 Cloudlet");
                AppendMenu(menu, 0x800, 0, "");
                AppendMenu(menu, 0, 2, "退出 Cloudlet 并卸载磁盘");
                GetCursorPos(out var position);
                SetForegroundWindow(hwnd);
                var command = TrackPopupMenu(menu, 0x0100 | 0x0002, position.X, position.Y, 0, hwnd, 0);
                DestroyMenu(menu);
                if (command == 1) _show();
                if (command == 2) _exit();
            }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    public void Dispose() { SetVisible(false); RemoveWindowSubclass(_window, _callback, 77); if (_icon != 0) DestroyIcon(_icon); _icon = 0; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nint wp, nint lp, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
