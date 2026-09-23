# RcloneLink.Core 公共接口（稳定契约）

Target net10.0; namespace `RcloneLink.Core`; all model classes have public parameterless constructors and settable properties. No third-party dependencies.

```csharp
class AppSettings {
 string RclonePath; // defaults AppContext.BaseDirectory/rclone.exe
 string ConfigPath; // defaults %APPDATA%/rclone/rclone.conf
 string Theme; // "System", "Light", "Dark"
 bool TrayVisible=true, CloseToTray=true, StartWithWindows=false, AutoMount=false;
 bool ShowTrayIcon; // alias of TrayVisible, not serialized separately
 Dictionary<string,string> DefaultMountOptions;
}
class MountProfile {
 string Id; // generated Guid string
 string Name, Remote, MountPoint;
 Dictionary<string,string> Options; // underscore keys as legacy Python, boolean values "true"/"false"
 bool AutoMount;
}
class AppState { AppSettings Settings; List<MountProfile> Mounts; }
class SettingsStore {
 SettingsStore(string? rootDirectory=null); // default %APPDATA%/RcloneLink/v2
 string StatePath { get; }
 AppState Load(); void Save(AppState state);
 AppState ImportLegacySettings(string settingsPath);
 List<MountProfile> ImportMounts(string jsonPath); // old flat mounts array or new AppState/mount array
 void ExportMounts(string jsonPath, IEnumerable<MountProfile> mounts);
}
record CommandResult(int ExitCode, string Output, string Error) {
 bool Success { get; } // ExitCode == 0
 bool Cancelled { get; init; }
}
class RcloneFile { string Name, Path; long Size; bool IsDir; DateTimeOffset? ModTime; }
class RcloneSize { long Count, Bytes; }
class RcloneService {
 RcloneService(AppSettings settings);
 AppSettings Settings { get; }
 Task<CommandResult> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken=default, IProgress<string>? progress=null);
 Task<string> GetVersionAsync(CancellationToken cancellationToken=default);
 Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken cancellationToken=default); // names WITHOUT colon
 Task<CommandResult> CreateWebDavAsync(string name,string url,string user,string password,string vendor="other",CancellationToken cancellationToken=default);
 Task<CommandResult> DeleteRemoteAsync(string name,CancellationToken cancellationToken=default);
 Task<IReadOnlyList<RcloneFile>> ListFilesAsync(string path,CancellationToken cancellationToken=default);
 Task<RcloneSize> GetSizeAsync(string path,CancellationToken cancellationToken=default);
 Task<CommandResult> CheckConnectionAsync(string remote,CancellationToken cancellationToken=default);
}
enum TransferOperation { Copy, Sync, Check }
class TransferRequest {
 TransferOperation Operation; string Source, Destination;
 bool DryRun=true, Checksum=false, IgnoreExisting=false, SizeOnly=false;
 string BandwidthLimit=""; int Transfers=4, Checkers=8;
 List<string> Includes, Excludes;
}
static class CommandBuilder {
 Dictionary<string,string> DefaultMountOptions();
 IReadOnlyList<string> BuildMount(MountProfile profile);
 IReadOnlyList<string> BuildTransfer(TransferRequest request);
 string NormalizeRemote(string remote); // name -> name:, name:path preserved, local paths unchanged
 void ValidateMount(MountProfile profile); // throws ArgumentException with Chinese messages
}
enum MountStatus { Stopped, Starting, Mounted, Stopping, Failed }
record MountState(string Id, MountStatus Status, string Message, int? ProcessId=null);
class MountManager : IAsyncDisposable {
 MountManager(RcloneService service);
 event EventHandler<MountState>? StatusChanged;
 event EventHandler<string>? OutputReceived;
 IReadOnlyList<MountState> GetStates();
 MountState GetState(string id);
 Task<MountState> StartAsync(MountProfile profile,CancellationToken cancellationToken=default);
 Task StopAsync(string id,CancellationToken cancellationToken=default);
 Task StopAllAsync(CancellationToken cancellationToken=default);
}
```

`RunAsync` does not throw for rclone nonzero or cancellation (returns Cancelled=true, ExitCode=-1); validation/start/JSON failures may throw, so UI handles errors. Convenience list/version/size methods throw InvalidOperationException on rclone nonzero. Load reads application state and the first-run legacy migration described below; it does not read rclone credentials. WebDAV create refuses duplicate names to avoid silent config overwrite. Password never appears in process arguments or returned logs. Mount verification requires a newly appeared Windows drive or mount reparse-point; preexisting drive/nonempty directory rejected. Stop targets only tracked process owned by this manager.

Default store migration: if v2/state.json is absent, Load reads the old %APPDATA%/RcloneLink/settings.json once and maps rclone_path, theme, tray_icon_hidden, default_mount_options and the file referenced by last_mount_list, then saves v2/state.json. Legacy files are never modified. Old executable-directory settings.json is not automatically searched. ImportLegacySettings provides an explicit import route for a selected legacy settings file. A custom rootDirectory disables automatic legacy discovery so tests and portable callers remain isolated.

Config create/delete backs up an existing rclone config beside itself with a timestamp and random suffix. WebDAV create uses a temporary rclone RC server bound to 127.0.0.1, randomly authenticated through child-only environment variables; credentials are sent in the HTTP JSON body and rclone stores obscured passwords. Transfer logs retain the last 1 MiB. Structured stdout above 64 MiB fails explicitly rather than truncating JSON.

StopAsync/StopAllAsync inspect private vfs/stats uploadsQueued, uploadsInProgress and erroredFiles, plus Dirty flags in that VFS's authenticated pathMeta directory. The metadata path is checked against the configured cache_dir/vfsMeta boundary; extended Windows paths are normalized and reparse subdirectories are skipped. Dirty files still held open block stopping even before they enter the upload queue. Pending work has a 30-second overall deadline. Failure or inability to inspect state leaves the mount running and throws. The UI must keep the application alive on that error. vfs/stats.inUse is a VFS reference count and is not an open-file count. Users should close editors before unmounting. Stop uses authenticated core/quit, with bounded fallback termination of the tracked process only after pending-upload checks. Source/target equality and ancestor/descendant transfer paths on local storage or the same named remote are rejected.
