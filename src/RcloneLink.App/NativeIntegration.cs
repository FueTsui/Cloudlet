using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using RcloneLink.Core;
using Windows.Storage.Pickers;

namespace RcloneLink.App;

public static class NativeIntegration
{
    public static bool IsWinFspInstalled()
        => GetWinFspVersion() != null;

    public static Version? GetWinFspVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key?.GetValue("InstallDir") is string location && File.Exists(Path.Combine(location, "bin", "winfsp-x64.dll")))
                return DependencyUpdates.ParseVersion(FileVersionInfo.GetVersionInfo(Path.Combine(location, "bin", "winfsp-x64.dll")).FileVersion ?? "0.0.0");
        }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll");
        return File.Exists(path) ? DependencyUpdates.ParseVersion(FileVersionInfo.GetVersionInfo(path).FileVersion ?? "0.0.0") : null;
    }

    public static string GetStartupStatus()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key?.GetValue("Cloudlet") is not string command) return "未注册登录启动；请在设置中开启并保存。";
        var expected = $"\"{Environment.ProcessPath}\" --minimized";
        if (!command.Equals(expected, StringComparison.OrdinalIgnoreCase)) return "启动项指向其他路径或参数不符；请重新保存设置。";
        foreach (var location in new[] { "Run", "Run32" })
        {
            using var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + location);
            if (approved?.GetValue("Cloudlet") is byte[] value && value.Length > 0 && (value[0] & 1) != 0)
                return "启动项已被 Windows 禁用；请在任务管理器的启动应用中启用 Cloudlet。";
        }
        return "登录启动已注册：后台静默运行，不显示主窗口。";
    }

    public static async Task<bool> InstallWinFspAsync(string path)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in new[] { "/i", path, "/passive", "/norestart" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 WinFsp 安装程序。");
        await process.WaitForExitAsync();
        if (process.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"WinFsp 安装未完成（{process.ExitCode}）。");
        return process.ExitCode == 3010;
    }

    public static void SetStartup(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 Cloudlet 程序路径。");
        if (!IsProductExecutable(executable)) throw new InvalidOperationException("当前程序未通过 Cloudlet 产品身份检查，无法修改开机启动项。");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var existing = key.GetValue("Cloudlet");
        if (existing != null && (existing is not string command || !IsProductStartupCommand(command)))
            throw new InvalidOperationException("Cloudlet 启动项名称已被无法核验的程序占用，现有启动项已保留。");
        if (enabled) key.SetValue("Cloudlet", $"\"{executable}\" --minimized", RegistryValueKind.String);
        else if (existing != null) key.DeleteValue("Cloudlet", false);
        // Called only by the user's Save Settings action. Unrelated entries,
        // scripts, missing files and commands with extra arguments are preserved.
        foreach (var legacyName in new[] { "RcloneLink", "RClone GUI" })
            if (key.GetValue(legacyName) is string legacy && IsProductStartupCommand(legacy)) key.DeleteValue(legacyName, false);
    }

    internal static bool IsProductStartupCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var arguments = CommandLineToArgvW(command, out var count);
        if (arguments == 0) return false;
        try
        {
            if (count is < 1 or > 2) return false;
            var executable = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments)) ?? "";
            if (count == 2 && !string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, IntPtr.Size)), "--minimized", StringComparison.Ordinal)) return false;
            return IsProductExecutable(Environment.ExpandEnvironmentVariables(executable));
        }
        finally { LocalFree(arguments); }
    }

    private static bool IsProductExecutable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
            var filename = Path.GetFileName(path);
            var product = filename.Equals("Cloudlet.exe", StringComparison.OrdinalIgnoreCase) ? "Cloudlet" : filename.Equals("RcloneLink.exe", StringComparison.OrdinalIgnoreCase) ? "RcloneLink" : "";
            if (product.Length == 0) return false;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, product, StringComparison.Ordinal) && string.Equals(info.CompanyName, "FueTsui", StringComparison.Ordinal) &&
                (string.Equals(info.OriginalFilename, product + ".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(info.OriginalFilename, product + ".dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static bool IsSystemTaskbarDark()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return settings?.GetValue("SystemUsesLightTheme") is not int value || value == 0;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static string GetThemeIconPath(bool darkBackground)
    {
        var themed = Path.Combine(AppContext.BaseDirectory, "Assets", darkBackground ? "icon-white.ico" : "icon-black.ico");
        return File.Exists(themed) ? themed : Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    public static void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) throw new FileNotFoundException("路径不存在。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new ArgumentException("仅支持 HTTPS 文档地址。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void OpenAuthorizationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/auth")
            throw new ArgumentException("授权地址必须来自本机配置向导。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static async Task<string?> PickFileAsync(Window owner, string title, string extension = "*")
    {
        var picker = new FileOpenPicker { CommitButtonText = title };
        picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> PickFolderAsync(Window owner)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> SaveFileAsync(Window owner, string title, string suggestedName, string extension = ".json")
    {
        var picker = new FileSavePicker { CommitButtonText = title, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("配置文件", new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static void InstallWinFsp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "winfsp-2.1.25156.msi");
        if (!File.Exists(path)) { OpenUrl("https://winfsp.dev/rel/"); return; }
        var info = new ProcessStartInfo("msiexec.exe") { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add("/i"); info.ArgumentList.Add(path);
        Process.Start(info);
    }
}
