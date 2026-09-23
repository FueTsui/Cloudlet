using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RcloneLink.Core;

public sealed partial class RcloneService(AppSettings settings)
{
    public AppSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<CommandResult> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        var arguments = args.ToArray();
        var sensitive = arguments.Length > 1 && arguments[0] == "config" && arguments[1] is "create" or "update" or "password" or "show" or "dump";
        using var process = new Process { StartInfo = CreateStartInfo(arguments) };
        if (cancellationToken.IsCancellationRequested) return new(-1, "", "操作已取消。") { Cancelled = true };
        process.Start();
        var boundedTail = arguments.Length > 0 && arguments[0] is "copy" or "sync" or "check";
        var stdout = ReadLinesAsync(process.StandardOutput, sensitive ? null : progress, boundedTail);
        var stderr = ReadLinesAsync(process.StandardError, sensitive ? null : progress, true);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
            return new(process.ExitCode, sensitive ? "" : output, sensitive && process.ExitCode != 0 ? "配置操作失败；详细输出已隐藏以保护凭证。" : sensitive ? "" : error);
        }
        catch (OperationCanceledException)
        {
            KillOwned(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new(-1, "", "操作已取消。") { Cancelled = true };
        }
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default) => RequireSuccess(await RunAsync(["version"], cancellationToken).ConfigureAwait(false)).Output.Trim();

    public async Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken cancellationToken = default)
    {
        var result = RequireSuccess(await RunAsync(["listremotes"], cancellationToken).ConfigureAwait(false));
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.TrimEnd(':')).ToArray();
    }

    public async Task<CommandResult> CreateWebDavAsync(string name, string url, string user, string password, string vendor = "other", CancellationToken cancellationToken = default)
    {
        ValidateRemoteName(name);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new ArgumentException("WebDAV 地址应为完整的 http 或 https 地址；请在独立字段填写凭证。");
        if (url.Any(c => c is '\r' or '\n' or '\0') || user.Any(c => c is '\r' or '\n' or '\0') || password.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("WebDAV 配置不能包含换行或空字符。");
        if (vendor is not ("other" or "nextcloud" or "owncloud" or "sharepoint" or "sharepoint-ntlm" or "rclone" or "fastmail" or "infinitescale")) throw new ArgumentException("WebDAV 服务类型无效。");
        if ((await ListRemotesAsync(cancellationToken).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("同名远程已存在；请使用其他名称，避免覆盖现有凭证。");
        if (string.IsNullOrWhiteSpace(Settings.ConfigPath)) throw new ArgumentException("请先设置 rclone 配置文件路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Settings.ConfigPath))!);
        BackupConfig();
        await using var session = StartRcProcess(["rcd", "--log-level", "ERROR"]);
        try
        {
            await session.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
            var parameters = new Dictionary<string, string> { ["url"] = url, ["user"] = user, ["pass"] = password, ["vendor"] = vendor };
            using var response = await session.PostAsync("config/create", new { name, type = "webdav", parameters, opt = new { obscure = true, nonInteractive = true } }, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? new(0, "WebDAV 配置已保存。", "") : new((int)response.StatusCode, "", "WebDAV 配置保存失败；服务详细输出已隐藏以保护凭证。");
        }
        catch (OperationCanceledException) { return new(-1, "", "操作已取消。") { Cancelled = true }; }
    }

    public Task<CommandResult> DeleteRemoteAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateRemoteName(name);
        BackupConfig();
        return RunAsync(["config", "delete", name], cancellationToken);
    }

    public async Task<IReadOnlyList<RcloneFile>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        CommandBuilder.ValidateText(path, "文件路径");
        var result = RequireSuccess(await RunAsync(["lsjson", path, "--no-mimetype"], cancellationToken).ConfigureAwait(false));
        return JsonSerializer.Deserialize<List<RcloneFile>>(result.Output, SettingsStore.JsonOptions) ?? [];
    }

    public async Task<RcloneSize> GetSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        CommandBuilder.ValidateText(path, "文件路径");
        var result = RequireSuccess(await RunAsync(["size", path, "--json"], cancellationToken).ConfigureAwait(false));
        return JsonSerializer.Deserialize<RcloneSize>(result.Output, SettingsStore.JsonOptions) ?? new();
    }

    public Task<CommandResult> CheckConnectionAsync(string remote, CancellationToken cancellationToken = default) => RunAsync(["lsf", CommandBuilder.NormalizeRemote(remote), "--max-depth", "1", "--dirs-only", "--contimeout", "10s", "--timeout", "20s", "--retries", "1", "--low-level-retries", "1"], cancellationToken);

    internal ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        CommandBuilder.ValidateText(Settings.RclonePath, "rclone 程序路径");
        var start = new ProcessStartInfo(Settings.RclonePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(Settings.ConfigPath)) { start.ArgumentList.Add("--config"); start.ArgumentList.Add(Settings.ConfigPath); }
        start.ArgumentList.Add("--ask-password=false");
        return start;
    }

    internal RcSession StartRcProcess(IEnumerable<string> args, IProgress<string>? progress = null, TimeSpan? requestTimeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var arguments = args.ToList();
        if (arguments[0] != "rcd") arguments.Add("--rc");
        arguments.AddRange(["--rc-addr", $"127.0.0.1:{port}"]);
        var start = CreateStartInfo(arguments);
        start.Environment["RCLONE_RC_USER"] = "rclonelink";
        start.Environment["RCLONE_RC_PASS"] = password;
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Start();
        return new(process, port, password, progress, requestTimeout);
    }

    internal static void ValidateRemoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[\p{L}\p{N}_][\p{L}\p{N}_. -]*$") || name.Length == 1 && char.IsLetter(name[0])) throw new ArgumentException("远程名称应以文字、数字或下划线开头；不能使用单个盘符字母或特殊符号。");
    }
    private void BackupConfig()
    {
        if (!string.IsNullOrWhiteSpace(Settings.ConfigPath) && File.Exists(Settings.ConfigPath))
            File.Copy(Settings.ConfigPath, Settings.ConfigPath + ".rclonelink-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
    }
    private static CommandResult RequireSuccess(CommandResult result) => result.Success ? result : throw new InvalidOperationException(result.Cancelled ? "操作已取消。" : string.IsNullOrWhiteSpace(result.Error) ? $"rclone 执行失败（{result.ExitCode}）。" : result.Error);
    internal static void KillOwned(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    private static async Task<string> ReadLinesAsync(StreamReader reader, IProgress<string>? progress, bool boundedTail = false)
    {
        var output = new StringBuilder();
        var truncated = false;
        var limit = boundedTail ? 1024 * 1024 : 64 * 1024 * 1024;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            progress?.Report(line);
            if (!truncated || boundedTail) output.AppendLine(line);
            if (output.Length > limit)
            {
                truncated = true;
                if (boundedTail) output.Remove(0, output.Length - limit);
                else output.Clear();
            }
        }
        if (truncated && !boundedTail) throw new InvalidDataException("rclone 输出超过 64 MiB，请缩小目录范围后重试。");
        return (truncated ? "[仅保留最近 1 MiB 输出]\n" : "") + output;
    }

    internal sealed class RcSession : IAsyncDisposable
    {
        public Process Process { get; }
        private readonly HttpClient _client;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private int _disposed;
        internal RcSession(Process process, int port, string password, IProgress<string>? progress, TimeSpan? requestTimeout = null)
        {
            Process = process;
            _client = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = requestTimeout ?? TimeSpan.FromSeconds(4) };
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("rclonelink:" + password)));
            _stdout = ReadLinesAsync(process.StandardOutput, progress, true);
            _stderr = ReadLinesAsync(process.StandardError, progress, true);
        }
        internal async Task WaitReadyAsync(CancellationToken cancellationToken)
        {
            for (var i = 0; i < 50; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Process.HasExited) throw new InvalidOperationException("rclone 服务提前退出，请检查程序与配置文件路径。");
                try { using var response = await PostAsync("core/version", new { }, cancellationToken).ConfigureAwait(false); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("rclone 本机控制服务启动超时。");
        }
        internal Task<HttpResponseMessage> PostAsync(string endpoint, object body, CancellationToken cancellationToken) => _client.PostAsync(endpoint, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), cancellationToken);
        internal async Task<string> GetErrorAsync() => await _stderr.ConfigureAwait(false);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (!Process.HasExited)
                {
                    using var quitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    try { using var response = await PostAsync("core/quit", new { exitCode = 0 }, quitTimeout.Token).ConfigureAwait(false); } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { KillOwned(Process); await Process.WaitForExitAsync().ConfigureAwait(false); }
                }
                await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
            }
            finally { _client.Dispose(); Process.Dispose(); }
        }
    }
}
