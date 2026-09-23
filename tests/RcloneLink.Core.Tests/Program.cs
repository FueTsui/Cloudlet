using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RcloneLink.Core;

var executable = args.FirstOrDefault(x => !x.StartsWith("--")) ?? Path.GetFullPath("rclone.exe");
var root = Path.Combine(Path.GetTempPath(), "RcloneLink-CoreTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var settings = new AppSettings { RclonePath = executable, ConfigPath = Path.Combine(root, "rclone.conf") };
var service = new RcloneService(settings);
var failures = new List<string>();
var passed = new List<string>();
var beforeProcesses = Process.GetProcessesByName("rclone").Select(p => p.Id).ToHashSet();
var source = Path.Combine(root, "source with spaces");
var destination = Path.Combine(root, "destination");
Directory.CreateDirectory(source);
await File.WriteAllTextAsync(Path.Combine(source, "中文 hello.txt"), "RcloneLink Unicode 验证\n");
await File.WriteAllTextAsync(Path.Combine(source, "ignore.tmp"), "excluded");
Directory.CreateDirectory(Path.Combine(source, "folder"));
await File.WriteAllTextAsync(Path.Combine(source, "folder", "nested.txt"), "nested data");

await Test("真实 rclone 版本", async () => Assert((await service.GetVersionAsync()).StartsWith("rclone v"), "version"));
await Test("更新版本比较与设置持久化", () =>
{
    Assert(DependencyUpdates.ParseVersion("rclone v1.75.1\n- os/windows") > DependencyUpdates.ParseVersion("rclone v1.70.3"), "version order");
    Assert(DependencyUpdates.ParseVersion("2.1.25156.0") == DependencyUpdates.ParseVersion("winfsp-2.1.25156.msi"), "WinFsp version normalization");
    var store = new SettingsStore(Path.Combine(root, "update-settings"));
    var state = new AppState();
    Assert(state.Settings.CheckDependencyUpdates && state.Settings.AutoUpdateRclone, "defaults");
    state.Settings.CheckDependencyUpdates = state.Settings.AutoUpdateRclone = false;
    store.Save(state);
    Assert(!store.Load().Settings.AutoUpdateRclone && !store.Load().Settings.CheckDependencyUpdates, "settings round trip");
    return Task.CompletedTask;
});
await Test("自动挂载跳过未选项并响应取消", async () =>
{
    await using var manager = new MountManager(service);
    await manager.StartAutomaticAsync([new MountProfile { AutoMount = false }]);
    Assert(manager.GetStates().Count == 0, "unselected started");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => manager.StartAutomaticAsync([new MountProfile { AutoMount = true }], cancellation.Token));
});
if (args.Contains("--updates"))
    await Test("官方版本检测、真实更新下载与安装包校验", async () =>
    {
        var updates = new DependencyUpdates();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var latest = await updates.CheckRcloneAsync(timeout.Token);
        var originalHash = Hash(executable);
        var olderEngine = Path.GetFullPath("artifacts/dependency-backup-1.70.3/rclone.exe");
        var updaterService = File.Exists(olderEngine) ? new RcloneService(new AppSettings { RclonePath = olderEngine, ConfigPath = settings.ConfigPath }) : service;
        var target = await updates.StageRcloneAsync(updaterService, latest, Path.Combine(root, "engines"), timeout.Token);
        Assert(Hash(executable) == originalHash && File.Exists(target), "original replaced");
        if (DependencyUpdates.ParseVersion(await service.GetVersionAsync()) == latest.Version)
            Assert(Hash(target) == originalHash, "supplied rclone differs from official download");
        var winFsp = await updates.CheckWinFspAsync(timeout.Token);
        var msi = await updates.DownloadWinFspAsync(winFsp, Path.Combine(root, "downloads"), timeout.Token);
        Assert(Hash(msi).Equals(winFsp.Sha256, StringComparison.OrdinalIgnoreCase), "installer hash");
        await Throws<InvalidDataException>(() => updates.DownloadWinFspAsync(winFsp with { Sha256 = new string('0', 64) }, Path.Combine(root, "bad-hash"), timeout.Token));
        Assert(!Directory.EnumerateFiles(Path.Combine(root, "bad-hash")).Any(), "bad download retained");
    });
await Test("真实 lsjson、size 和连接检查", async () =>
{
    var files = await service.ListFilesAsync(source);
    Assert(files.Any(f => f.Name == "中文 hello.txt" && !f.IsDir), "Unicode listing");
    Assert(files.Any(f => f.Name == "folder" && f.IsDir), "directory listing");
    var size = await service.GetSizeAsync(source);
    Assert(size.Count == 3 && size.Bytes > 20, "size");
    Assert((await service.CheckConnectionAsync(source)).Success, "connection");
});
await Test("复制 dry-run 不修改目标、过滤及内容哈希", async () =>
{
    var request = new TransferRequest { Source = source, Destination = destination, Excludes = ["*.tmp"] };
    Assert((await service.RunAsync(CommandBuilder.BuildTransfer(request))).Success, "dryrun result");
    Assert(!Directory.Exists(destination), "dryrun created destination");
    request.DryRun = false;
    Assert((await service.RunAsync(CommandBuilder.BuildTransfer(request))).Success, "copy result");
    Assert(!File.Exists(Path.Combine(destination, "ignore.tmp")), "exclude");
    Assert(Hash(Path.Combine(source, "中文 hello.txt")) == Hash(Path.Combine(destination, "中文 hello.txt")), "hash mismatch");
});
await Test("sync 预演保留多余文件，真实 sync 清理测试文件", async () =>
{
    var extra = Path.Combine(destination, "extra.txt");
    await File.WriteAllTextAsync(extra, "delete only in isolated fixture");
    var request = new TransferRequest { Operation = TransferOperation.Sync, Source = source, Destination = destination, DryRun = true };
    Assert((await service.RunAsync(CommandBuilder.BuildTransfer(request))).Success, "sync dryrun");
    Assert(File.Exists(extra), "sync dryrun deleted");
    request.DryRun = false;
    Assert((await service.RunAsync(CommandBuilder.BuildTransfer(request))).Success, "sync");
    Assert(!File.Exists(extra), "sync did not delete extra");
});
await Test("check 校验一致与不一致退出码", async () =>
{
    var request = new TransferRequest { Operation = TransferOperation.Check, Source = source, Destination = destination, DryRun = false, Checksum = true };
    Assert((await service.RunAsync(CommandBuilder.BuildTransfer(request))).Success, "check matching");
    await File.AppendAllTextAsync(Path.Combine(destination, "中文 hello.txt"), "changed");
    Assert(!(await service.RunAsync(CommandBuilder.BuildTransfer(request))).Success, "check mismatch detection");
});
await Test("命令取消等待进程退出", async () =>
{
    var large = Path.Combine(root, "large-source");
    Directory.CreateDirectory(large);
    await File.WriteAllBytesAsync(Path.Combine(large, "large.bin"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var timer = Stopwatch.StartNew();
    var result = await service.RunAsync(CommandBuilder.BuildTransfer(new TransferRequest { Source = large, Destination = Path.Combine(root, "cancel-target"), DryRun = false, BandwidthLimit = "16k" }), cancel.Token);
    Assert(result.Cancelled && !result.Success && timer.Elapsed < TimeSpan.FromSeconds(10), "cancel result");
});
await Test("WebDAV 真实隔离配置、无明文密码、重名保护与备份", async () =>
{
    var password = "TestOnly-!密碼-" + Guid.NewGuid().ToString("N");
    var result = await service.CreateWebDavAsync("test-webdav", "https://example.invalid/dav/", "tester", password, "infinitescale");
    Assert(result.Success, "create: " + result.Error);
    Assert(!result.Output.Contains(password) && !result.Error.Contains(password), "returned credential");
    Assert((await service.ListRemotesAsync()).Contains("test-webdav"), "remote not saved");
    Assert(!File.ReadAllText(settings.ConfigPath).Contains(password), "plain password persisted");
    var before = Hash(settings.ConfigPath);
    await Throws<ArgumentException>(() => service.CreateWebDavAsync("test-webdav", "https://example.invalid/", "x", "y"));
    Assert(before == Hash(settings.ConfigPath), "duplicate changed config");
    Assert((await service.CreateWebDavAsync("preserved-remote", "https://example.invalid/", "second", "second-test-only")).Success, "second remote");
    Assert((await service.DeleteRemoteAsync("test-webdav")).Success, "delete");
    Assert((await service.ListRemotesAsync()).SequenceEqual(["preserved-remote"]), "delete touched unrelated remote");
    Assert(Directory.GetFiles(root, "rclone.conf.rclonelink-backup-*").Length >= 2, "config backup absent");
});
await Test("本机 WebDAV 认证、连接检查、浏览与下载哈希闭环", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    var url = $"http://127.0.0.1:{port}/";
    var serverPassword = "local-test-" + Guid.NewGuid().ToString("N");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "serve", "webdav", source, "--addr", $"127.0.0.1:{port}", "--user", "local-test-user", "--config", settings.ConfigPath, "--log-level", "ERROR", "--ask-password=false" }) start.ArgumentList.Add(argument);
    start.Environment["RCLONE_PASS"] = serverPassword;
    using var server = Process.Start(start) ?? throw new InvalidOperationException("WebDAV fixture could not start");
    var serverOut = server.StandardOutput.ReadToEndAsync();
    var serverError = server.StandardError.ReadToEndAsync();
    try
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var ready = false;
        for (var i = 0; i < 60 && !ready; i++)
        {
            if (server.HasExited) throw new InvalidOperationException("WebDAV fixture exited prematurely");
            try { using var response = await client.GetAsync(url); ready = response.StatusCode == HttpStatusCode.Unauthorized; }
            catch (HttpRequestException) { }
            if (!ready) await Task.Delay(100);
        }
        Assert(ready, "unauthenticated request must be rejected by ready server");
        Assert((await service.CreateWebDavAsync("live-webdav", url, "local-test-user", serverPassword)).Success, "create live config");
        Assert((await service.CheckConnectionAsync("live-webdav")).Success, "live WebDAV authenticated connection");
        Assert((await service.ListFilesAsync("live-webdav:")).Any(f => f.Name == "中文 hello.txt"), "live WebDAV authenticated listing");
        var downloaded = Path.Combine(root, "webdav-download");
        var copy = await service.RunAsync(CommandBuilder.BuildTransfer(new() { Source = "live-webdav:", Destination = downloaded, DryRun = false }));
        Assert(copy.Success, "WebDAV download: " + copy.Error);
        Assert(Hash(Path.Combine(source, "中文 hello.txt")) == Hash(Path.Combine(downloaded, "中文 hello.txt")), "WebDAV downloaded hash mismatch");
    }
    finally
    {
        if (!server.HasExited) server.Kill(entireProcessTree: true);
        await server.WaitForExitAsync();
        await Task.WhenAll(serverOut, serverError);
    }
});
await Test("通用 provider 目录与 SFTP、S3、local 图形配置协议", async () =>
{
    var providers = await service.GetProvidersAsync();
    Assert(providers.Count > 40 && providers.Any(p => p.Name == "drive"), "provider catalog incomplete");
    Assert(providers.Single(p => p.Name == "sftp").Options.Any(o => o.Name == "pass" && o.IsPassword), "SFTP password metadata");
    Assert(providers.Single(p => p.Name == "s3").Options.Any(o => o.Name == "provider" && o.Examples.Count > 3), "S3 examples metadata");
    var original = File.ReadAllBytes(settings.ConfigPath);
    var password = "sftp-test-only-" + Guid.NewGuid().ToString("N");
    await using (var sftp = await service.BeginProviderConfigurationAsync("wizard-sftp", "sftp", new Dictionary<string, string> { ["host"] = "example.invalid", ["user"] = "test-only", ["pass"] = password }))
    {
        Assert(sftp.CurrentStep.Complete, "SFTP should finish without network");
        Assert(File.ReadAllBytes(settings.ConfigPath).SequenceEqual(original), "draft changed target config before commit");
        await sftp.CommitAsync();
        Assert(sftp.IsCommitted, "commit state");
    }
    var sftpConfig = File.ReadAllText(settings.ConfigPath);
    Assert(!sftpConfig.Contains(password), "SFTP password stored plaintext");
    Assert(File.ReadAllBytes(settings.ConfigPath).AsSpan(0, original.Length).SequenceEqual(original), "existing config bytes not preserved");
    await using (var s3 = await service.BeginProviderConfigurationAsync("wizard-s3", "s3", new Dictionary<string, string> { ["provider"] = "Other", ["env_auth"] = "false", ["access_key_id"] = "test-only-id", ["secret_access_key"] = "test-only-secret", ["endpoint"] = "https://example.invalid" }))
    { Assert(s3.CurrentStep.Complete, "S3 unexpected post config"); await s3.CommitAsync(); }
    await using (var local = await service.BeginProviderConfigurationAsync("wizard-local", "local", new Dictionary<string, string>()))
    { Assert(local.CurrentStep.Complete, "local unexpected post config"); await local.CommitAsync(); }
    var listed = await service.ListFilesAsync("wizard-local:" + source.Replace('\\', '/'));
    Assert(listed.Any(f => f.Name == "中文 hello.txt"), "non-webdav provider real local listing");
});
await Test("provider 草稿取消、重名与并发修改保护", async () =>
{
    var before = File.ReadAllBytes(settings.ConfigPath);
    await using (var cancelled = await service.BeginProviderConfigurationAsync("cancelled-provider", "local", new Dictionary<string, string>()))
        Assert(cancelled.CurrentStep.Complete, "cancel fixture incomplete");
    Assert(File.ReadAllBytes(settings.ConfigPath).SequenceEqual(before), "cancel changed config");
    Assert(!(await service.ListRemotesAsync()).Contains("cancelled-provider"), "cancel left remote");
    await Throws<ArgumentException>(() => service.BeginProviderConfigurationAsync("wizard-local", "local", new Dictionary<string, string>()));
    await using (var concurrent = await service.BeginProviderConfigurationAsync("conflict-provider", "local", new Dictionary<string, string>()))
    {
        await File.AppendAllTextAsync(settings.ConfigPath, "\n# external update preserved\n");
        var changed = File.ReadAllBytes(settings.ConfigPath);
        await Throws<InvalidOperationException>(() => concurrent.CommitAsync());
        Assert(File.ReadAllBytes(settings.ConfigPath).SequenceEqual(changed), "concurrent change overwritten");
    }
    var beforeCancelCommit = File.ReadAllBytes(settings.ConfigPath);
    var commitCancellation = await service.BeginProviderConfigurationAsync("cancel-during-commit", "local", new Dictionary<string, string>());
    var committing = commitCancellation.CommitAsync();
    await commitCancellation.DisposeAsync();
    await Throws<OperationCanceledException>(() => committing);
    Assert(File.ReadAllBytes(settings.ConfigPath).SequenceEqual(beforeCancelCommit), "dispose racing commit changed original");
});
await Test("OAuth 非交互后续提问与无授权取消保留原配置", async () =>
{
    var before = File.ReadAllBytes(settings.ConfigPath);
    await using (var oauth = await service.BeginProviderConfigurationAsync("cancelled-drive", "drive", new Dictionary<string, string>()))
    {
        if (oauth.CurrentStep.Option?.Name == "config_shared_client_id") await oauth.AdvanceAsync("true");
        Assert(!oauth.CurrentStep.Complete && oauth.CurrentStep.Option?.Name == "config_is_local", "OAuth initial question");
        var step = await oauth.AdvanceAsync("false");
        Assert(!step.Complete && step.Option is not null && step.Option.Name == "config_token", "OAuth external token question");
        Assert(step.Option!.Sensitive, "OAuth token should use masked input");
        await Throws<InvalidOperationException>(() => oauth.CommitAsync());
    }
    Assert(File.ReadAllBytes(settings.ConfigPath).SequenceEqual(before), "cancelled OAuth changed original config");
});
await Test("OAuth 本机授权持续等待超过4秒，取消回收专属进程", async () =>
{
    var before = File.ReadAllBytes(settings.ConfigPath);
    await using var oauth = await service.BeginProviderConfigurationAsync("pending-drive", "drive", new Dictionary<string, string>(), openBrowser: false);
    if (oauth.CurrentStep.Option?.Name == "config_shared_client_id") await oauth.AdvanceAsync("true");
    var authorization = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    oauth.AuthorizationUrlAvailable += (_, url) => authorization.TrySetResult(url);
    using var cancellation = new CancellationTokenSource();
    var pending = oauth.AdvanceAsync("true", cancellation.Token);
    try
    {
        var url = await authorization.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.AbsolutePath == "/auth", "OAuth fallback URL not localhost auth");
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert(!pending.IsCompleted, "OAuth request stopped at the old four-second HTTP timeout");
    }
    finally
    {
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => pending);
    }
    Assert(File.ReadAllBytes(settings.ConfigPath).SequenceEqual(before), "cancelled pending OAuth changed original config");
});
await Test("旧版完整挂载导入、设置迁移和原子保存往返", async () =>
{
    var legacy = Path.Combine(root, "legacy-mounts.json");
    var options = CommandBuilder.DefaultMountOptions().ToDictionary(p => p.Key, p => (object)p.Value);
    options["remote_name"] = "legacy"; options["mount_point"] = "Q:"; options["no_check_certificate"] = true;
    await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(new[] { options }));
    var store = new SettingsStore(Path.Combine(root, "state"));
    var imported = store.ImportMounts(legacy);
    Assert(imported.Count == 1 && imported[0].Options["no_check_certificate"] == "true" && imported[0].Options["vfs_cache_mode"] == "full", "legacy import");
    var legacySettings = Path.Combine(root, "settings.json");
    await File.WriteAllTextAsync(legacySettings, JsonSerializer.Serialize(new { rclone_path = executable, theme = "深色", tray_icon_hidden = true, last_mount_list = legacy }));
    var oldHash = Hash(legacySettings);
    var state = store.ImportLegacySettings(legacySettings);
    Assert(state.Settings.Theme == "Dark" && !state.Settings.TrayVisible && state.Mounts.Count == 1, "legacy settings");
    state.Settings.ConfigPath = settings.ConfigPath;
    store.Save(state);
    Assert(store.Load().Mounts[0].Id == state.Mounts[0].Id, "state roundtrip");
    Assert(Hash(legacySettings) == oldHash, "legacy was modified");
    var exported = Path.Combine(root, "exported.json");
    store.ExportMounts(exported, state.Mounts);
    Assert(store.ImportMounts(exported)[0].Remote == "legacy", "export roundtrip");
});
await Test("参数拒绝注入、嵌套复制和冲突校验", async () =>
{
    await Throws<ArgumentException>(() => Task.FromResult(CommandBuilder.BuildTransfer(new() { Source = "--config", Destination = destination })));
    await Throws<ArgumentException>(() => Task.FromResult(CommandBuilder.BuildTransfer(new() { Source = source, Destination = Path.Combine(source, "nested") })));
    await Throws<ArgumentException>(() => Task.FromResult(CommandBuilder.BuildTransfer(new() { Source = "test-remote:folder", Destination = "test-remote:folder/sub" })));
    await Throws<ArgumentException>(() => Task.FromResult(CommandBuilder.BuildTransfer(new() { Source = source, Destination = destination, Checksum = true, SizeOnly = true })));
    var profile = new MountProfile { Remote = "test", MountPoint = "Q:" };
    profile.Options["exec"] = "bad";
    await Throws<ArgumentException>(() => Task.FromResult(CommandBuilder.BuildMount(profile)));
});

if (OperatingSystem.IsWindows() && !args.Contains("--skip-mount"))
    await Test("真实 WinFsp 挂载、盘符读写、缓存写回与卸载", async () =>
    {
        var drive = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => ((char)c) + ":").First(d => !DriveInfo.GetDrives().Any(x => x.Name.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
        var profile = new MountProfile { Name = "isolated local mount", Remote = source, MountPoint = drive };
        profile.Options["cache_dir"] = Path.Combine(root, "mount-cache");
        profile.Options["vfs_cache_max_size"] = "64M";
        profile.Options["buffer_size"] = "1M";
        profile.Options["vfs_read_ahead"] = "1M";
        profile.Options["vfs_read_chunk_size"] = "1M";
        profile.Options["vfs_cache_poll_interval"] = "1s";
        await using var manager = new MountManager(service);
        var mount = await manager.StartAsync(profile);
        Assert(mount.Status == MountStatus.Mounted, mount.Message);
        var processId = mount.ProcessId!.Value;
        Assert(Hash(Path.Combine(drive + "\\", "中文 hello.txt")) == Hash(Path.Combine(source, "中文 hello.txt")), "mounted read hash");
        var heldPath = Path.Combine(drive + "\\", "held-open.bin");
        var heldBytes = RandomNumberGenerator.GetBytes(4096);
        await using (var held = new FileStream(heldPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            await held.WriteAsync(heldBytes);
            await held.FlushAsync();
            using var stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Throws<OperationCanceledException>(() => manager.StopAsync(profile.Id, stopCancellation.Token));
            Assert(manager.GetState(profile.Id).Status == MountStatus.Mounted, "dirty open file stop changed mount state");
            Assert(Process.GetProcessesByName("rclone").Any(p => p.Id == processId), "dirty open file process was terminated");
        }
        var heldUnderlying = Path.Combine(source, "held-open.bin");
        for (var i = 0; i < 100 && !File.Exists(heldUnderlying); i++) await Task.Delay(200);
        Assert(File.Exists(heldUnderlying) && Hash(heldUnderlying) == Convert.ToHexString(SHA256.HashData(heldBytes)), "closed dirty file was not written back");
        var write = RandomNumberGenerator.GetBytes(8192);
        await File.WriteAllBytesAsync(Path.Combine(drive + "\\", "through-mount.bin"), write);
        var underlying = Path.Combine(source, "through-mount.bin");
        var expected = Convert.ToHexString(SHA256.HashData(write));
        for (var i = 0; i < 100 && (!File.Exists(underlying) || Hash(underlying) != expected); i++) await Task.Delay(200);
        Assert(File.Exists(underlying) && Hash(underlying) == expected, "cache not written to underlying fixture");
        await manager.StopAsync(profile.Id);
        Assert(manager.GetState(profile.Id).Status == MountStatus.Stopped, "stop state");
        Assert(!DriveInfo.GetDrives().Any(d => d.Name.StartsWith(drive, StringComparison.OrdinalIgnoreCase)), "drive remained");
        Assert(!Process.GetProcessesByName("rclone").Any(p => p.Id == processId), "owned mount process remained");
    });

await Task.Delay(300);
await Test("无本轮 rclone 子进程残留", () =>
{
    var after = Process.GetProcessesByName("rclone").Select(p => p.Id).Where(id => !beforeProcesses.Contains(id)).ToArray();
    Assert(after.Length == 0, "remaining process IDs: " + string.Join(",", after));
    return Task.CompletedTask;
});
var report = new { passed, failures, fixture = root, completedUtc = DateTime.UtcNow };
await File.WriteAllTextAsync(Path.Combine(root, "test-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {passed.Count} passed, {failures.Count} failed. Fixture: {root}");
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> run)
{
    try { await run(); passed.Add(name); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
