using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RcloneLink.App;

/// <summary>Owns native icons independently for the caption and taskbar surfaces.</summary>
internal sealed class NativeWindowIcons : IDisposable
{
    private readonly nint _window;
    private nint _smallIcon;
    private nint _largeIcon;
    private uint _dpi;
    private bool _disposed;
    internal string SmallIconPath { get; private set; } = "";
    internal string LargeIconPath { get; private set; } = "";
    internal nint SmallIconHandle => SendMessage(_window, 0x007F, 0, 0);
    internal nint LargeIconHandle => SendMessage(_window, 0x007F, 1, 0);
    internal bool NativeHandlesMatch => _smallIcon != 0 && _largeIcon != 0 && SmallIconHandle == _smallIcon && LargeIconHandle == _largeIcon;

    internal NativeWindowIcons(nint window) => _window = window;

    internal void Update(bool darkWindow, bool darkTaskbar)
    {
        if (_disposed) return;
        var smallPath = NativeIntegration.GetThemeIconPath(darkWindow);
        var largePath = NativeIntegration.GetThemeIconPath(darkTaskbar);
        var dpi = GetDpiForWindow(_window);
        if (dpi == 0) dpi = 96;
        if (smallPath == SmallIconPath && largePath == LargeIconPath && _dpi == dpi && _smallIcon != 0 && _largeIcon != 0) return;
        var smallSize = Math.Max(16, GetSystemMetricsForDpi(49, dpi));
        var largeSize = Math.Max(32, GetSystemMetricsForDpi(11, dpi));
        var newSmall = LoadImage(0, smallPath, 1, smallSize, smallSize, 0x10);
        if (newSmall == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 Cloudlet 窗口图标。");
        var newLarge = LoadImage(0, largePath, 1, largeSize, largeSize, 0x10);
        if (newLarge == 0)
        {
            DestroyIcon(newSmall);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 Cloudlet 任务栏图标。");
        }
        SendMessage(_window, 0x0080, 0, newSmall);
        SendMessage(_window, 0x0080, 1, newLarge);
        if (_smallIcon != 0) DestroyIcon(_smallIcon);
        if (_largeIcon != 0) DestroyIcon(_largeIcon);
        _smallIcon = newSmall;
        _largeIcon = newLarge;
        SmallIconPath = smallPath;
        LargeIconPath = largePath;
        _dpi = dpi;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsWindow(_window))
        {
            SendMessage(_window, 0x0080, 0, 0);
            SendMessage(_window, 0x0080, 1, 0);
        }
        if (_smallIcon != 0) DestroyIcon(_smallIcon);
        if (_largeIcon != 0) DestroyIcon(_largeIcon);
        _smallIcon = _largeIcon = 0;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
