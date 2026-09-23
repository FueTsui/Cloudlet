using System.Text.Json;
using System.Text.RegularExpressions;

namespace RcloneLink.Core;

public sealed class MountManager(RcloneService service) : IAsyncDisposable
{
    private sealed record ActiveMount(MountProfile Profile, RcloneService.RcSession Session);
    private readonly Dictionary<string, ActiveMount> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MountState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    public event EventHandler<MountState>? StatusChanged;
    public event EventHandler<string>? OutputReceived;

    public IReadOnlyList<MountState> GetStates() { lock (_stateLock) return _states.Values.ToArray(); }
    public MountState GetState(string id) { lock (_stateLock) return _states.GetValueOrDefault(id) ?? new(id, MountStatus.Stopped, "未挂载"); }

    public async Task StartAutomaticAsync(IEnumerable<MountProfile> profiles, CancellationToken token = default)
    {
        var pending = profiles.Where(p => p.AutoMount).ToList();
        for (var attempt = 0; attempt < 3 && pending.Count > 0; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(10 * attempt), token);
            foreach (var profile in pending.ToArray())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var state = await StartAsync(profile, token);
                    if (state.Status == MountStatus.Mounted) pending.Remove(profile);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { SetState(new(profile.Id, MountStatus.Failed, ex.Message)); }
            }
        }
    }

    public async Task<MountState> StartAsync(MountProfile profile, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommandBuilder.ValidateMount(profile);
            if (_active.ContainsKey(profile.Id)) return GetState(profile.Id);
            if (_active.Values.Any(m => m.Profile.MountPoint.TrimEnd('\\', '/').Equals(profile.MountPoint.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("此挂载点已由另一个挂载使用。");
            if (IsDrive(profile.MountPoint))
            {
                if (DriveInfo.GetDrives().Any(d => d.Name.StartsWith(profile.MountPoint[..2], StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("所选盘符已占用，请换用空闲盘符。");
            }
            else if (Directory.Exists(profile.MountPoint))
            {
                if ((File.GetAttributes(profile.MountPoint) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(profile.MountPoint).Any()) throw new ArgumentException("挂载目录必须为空且不能已经是挂载点或符号链接。");
            }
            else if (File.Exists(profile.MountPoint)) throw new ArgumentException("挂载点已存在同名文件。");
            SetState(new(profile.Id, MountStatus.Starting, "正在启动并验证挂载…"));
            RcloneService.RcSession? session = null;
            try
            {
                session = service.StartRcProcess(CommandBuilder.BuildMount(profile), new DirectProgress(line => OutputReceived?.Invoke(this, line)));
                _active.Add(profile.Id, new(profile, session));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await session.WaitReadyAsync(timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (session.Process.HasExited) throw new InvalidOperationException("挂载进程提前退出：" + await session.GetErrorAsync().ConfigureAwait(false));
                    if (IsMounted(profile.MountPoint)) break;
                    await Task.Delay(200, timeout.Token).ConfigureAwait(false);
                }
                var mounted = new MountState(profile.Id, MountStatus.Mounted, "已挂载并验证", session.Process.Id);
                SetState(mounted);
                _ = ObserveExitAsync(profile.Id, session);
                return mounted;
            }
            catch (Exception ex)
            {
                _active.Remove(profile.Id);
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                var message = ex is OperationCanceledException ? cancellationToken.IsCancellationRequested ? "挂载已取消。" : "30 秒内未检测到真实挂载点，已停止本次进程。" : ex.Message;
                var failed = new MountState(profile.Id, MountStatus.Failed, message);
                SetState(failed);
                return failed;
            }
        }
        finally { _operations.Release(); }
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active.TryGetValue(id, out var active)) { SetState(new(id, MountStatus.Stopped, "未挂载")); return; }
            if (!active.Session.Process.HasExited)
            {
                SetState(new(id, MountStatus.Stopping, "正在等待写入缓存完成…", active.Session.Process.Id));
                try { await WaitForUploadsAsync(active.Session, active.Profile, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    SetState(new(id, MountStatus.Mounted, "未停止：" + ex.Message, active.Session.Process.Id));
                    throw;
                }
            }
            await active.Session.DisposeAsync().ConfigureAwait(false);
            _active.Remove(id);
            for (var i = 0; i < 50 && IsMounted(active.Profile.MountPoint); i++) await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            if (IsMounted(active.Profile.MountPoint))
            {
                SetState(new(id, MountStatus.Failed, "本次进程已退出，但挂载点仍可见，请在资源管理器中检查。"));
                throw new IOException("挂载进程已退出，挂载点仍可见。");
            }
            SetState(new(id, MountStatus.Stopped, "已停止挂载"));
        }
        finally { _operations.Release(); }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        string[] ids;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ids = _active.Keys.ToArray(); } finally { _operations.Release(); }
        List<Exception> errors = [];
        foreach (var id in ids) { try { await StopAsync(id, cancellationToken).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); } }
        if (errors.Count > 0) throw new AggregateException("部分挂载尚不能安全停止，应用应保持运行直到待上传文件完成。", errors);
    }

    private static async Task WaitForUploadsAsync(RcloneService.RcSession session, MountProfile profile, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { await WaitForUploadsCoreAsync(session, profile, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("30 秒内仍未确认所有缓存写入完成，请关闭编辑器并等待上传完成后重试。"); }
    }

    private static async Task WaitForUploadsCoreAsync(RcloneService.RcSession session, MountProfile profile, CancellationToken cancellationToken)
    {
        // An empty queue is checked twice to avoid stopping during the write-back delay.
        var stable = 0;
        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Process.HasExited) return;
            using var response = await session.PostAsync("vfs/stats", new { }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("无法核实缓存上传状态，为保护写入数据保留挂载。");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var busy = false;
            if (doc.RootElement.TryGetProperty("diskCache", out var disk))
            {
                foreach (var key in new[] { "uploadsQueued", "uploadsInProgress", "erroredFiles" })
                    if (disk.TryGetProperty(key, out var count) && count.TryGetInt64(out var number) && number > 0) busy = true;
                // Open modified files are not in the upload queue until closed. rclone persists
                // Dirty on first write, so check only this VFS's authenticated metadata path.
                if (!disk.TryGetProperty("pathMeta", out var metadata) || metadata.GetString() is not { Length: > 0 } metadataPath)
                    throw new InvalidOperationException("无法确定本次挂载的缓存元数据位置，已保留挂载。");
                busy |= await HasDirtyMetadataAsync(metadataPath, profile, cancellationToken).ConfigureAwait(false);
            }
            // vfs/stats.inUse counts VFS references (normally 1 for the mount), not open files.
            stable = busy ? 0 : stable + 1;
            if (stable >= 2) return;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("30 秒内仍有打开的脏文件、待上传或上传失败的文件，请关闭编辑器并等待上传完成后重试。");
    }

    private static async Task<bool> HasDirtyMetadataAsync(string metadataPath, MountProfile profile, CancellationToken cancellationToken)
    {
        var cache = profile.Options.TryGetValue("cache_dir", out var configured) && !string.IsNullOrWhiteSpace(configured) ? configured : CommandBuilder.DefaultMountOptions()["cache_dir"];
        var metadataRoot = NormalizeMetadataPath(Path.Combine(cache, "vfsMeta")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = NormalizeMetadataPath(metadataPath);
        if (!target.StartsWith(metadataRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("缓存元数据路径与本次挂载配置不符，无法安全停止。");
        if (!Directory.Exists(target)) return false;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var file in Directory.EnumerateFiles(target, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidOperationException("缓存元数据异常过大，无法安全确认写入状态。");
                using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!metadata.RootElement.TryGetProperty("Dirty", out var dirty) || dirty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException("缓存元数据格式无法识别，已保留挂载。");
                if (dirty.GetBoolean()) return true;
            }
            catch (FileNotFoundException) { /* A completed cache item may be removed while scanning. */ }
            catch (DirectoryNotFoundException) { }
            catch (JsonException) { return true; /* rclone may be atomically rewriting this item; retry. */ }
        }
        return false;
    }

    private static string NormalizeMetadataPath(string path)
    {
        // rclone returns extended-length Windows paths; compare their canonical filesystem form.
        if (OperatingSystem.IsWindows() && path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (OperatingSystem.IsWindows() && path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path);
    }

    private async Task ObserveExitAsync(string id, RcloneService.RcSession session)
    {
        try
        {
            await session.Process.WaitForExitAsync().ConfigureAwait(false);
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_active.TryGetValue(id, out var active) && ReferenceEquals(active.Session, session))
                {
                    var code = session.Process.ExitCode;
                    var error = await session.GetErrorAsync().ConfigureAwait(false);
                    _active.Remove(id);
                    await session.DisposeAsync().ConfigureAwait(false);
                    SetState(new(id, code == 0 ? MountStatus.Stopped : MountStatus.Failed, code == 0 ? "挂载进程已退出" : $"挂载异常退出（{code}）：{error}"));
                }
            }
            finally { _operations.Release(); }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    private void SetState(MountState state) { lock (_stateLock) _states[state.Id] = state; StatusChanged?.Invoke(this, state); }
    private static bool IsDrive(string path) => Regex.IsMatch(path, @"^[A-Za-z]:[\\/]?$");
    private static bool IsMounted(string path)
    {
        try
        {
            if (IsDrive(path)) return DriveInfo.GetDrives().Any(d => d.Name.StartsWith(path[..2], StringComparison.OrdinalIgnoreCase)) && Directory.Exists(path[..2] + "\\");
            return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException) { return false; }
    }
    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
    private sealed class DirectProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
