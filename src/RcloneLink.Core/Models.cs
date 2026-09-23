using System.Text.Json.Serialization;

namespace RcloneLink.Core;

public sealed class AppSettings
{
    public string RclonePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "rclone.exe");
    public string ConfigPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rclone", "rclone.conf");
    public string Theme { get; set; } = "System";
    public bool TrayVisible { get; set; } = true;
    [JsonIgnore] public bool ShowTrayIcon { get => TrayVisible; set => TrayVisible = value; }
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoMount { get; set; }
    public bool CheckDependencyUpdates { get; set; } = true;
    public bool AutoUpdateRclone { get; set; } = true;
    public Dictionary<string, string> DefaultMountOptions { get; set; } = CommandBuilder.DefaultMountOptions();
}

public sealed class MountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Remote { get; set; } = "";
    public string MountPoint { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = CommandBuilder.DefaultMountOptions();
    public bool AutoMount { get; set; }
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<MountProfile> Mounts { get; set; } = [];
}

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0 && !Cancelled;
    public bool Cancelled { get; init; }
}

public sealed class RcloneFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public DateTimeOffset? ModTime { get; set; }
}

public sealed class RcloneSize
{
    public long Count { get; set; }
    public long Bytes { get; set; }
}

public enum TransferOperation { Copy, Sync, Check }

public sealed class TransferRequest
{
    public TransferOperation Operation { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public bool Checksum { get; set; }
    public bool IgnoreExisting { get; set; }
    public bool SizeOnly { get; set; }
    public string BandwidthLimit { get; set; } = "";
    public int Transfers { get; set; } = 4;
    public int Checkers { get; set; } = 8;
    public List<string> Includes { get; set; } = [];
    public List<string> Excludes { get; set; } = [];
}

public enum MountStatus { Stopped, Starting, Mounted, Stopping, Failed }
public sealed record MountState(string Id, MountStatus Status, string Message, int? ProcessId = null);
