using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RcloneLink.Core;

public sealed class RcloneProvider
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prefix { get; set; } = "";
    public List<RcloneProviderOption> Options { get; set; } = [];
    public bool Hide { get; set; }
}

public sealed class RcloneProviderOption
{
    public string Name { get; set; } = "";
    public string Help { get; set; } = "";
    public string Type { get; set; } = "";
    public string Provider { get; set; } = "";
    public JsonElement Default { get; set; }
    public string DefaultStr { get; set; } = "";
    public string DefaultText => !string.IsNullOrEmpty(DefaultStr) ? DefaultStr : Default.ValueKind switch { JsonValueKind.String => Default.GetString() ?? "", JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => Default.GetRawText(), _ => "" };
    public bool Required { get; set; }
    public bool IsPassword { get; set; }
    public bool Sensitive { get; set; }
    public bool Advanced { get; set; }
    public bool Exclusive { get; set; }
    public int Hide { get; set; }
    public List<RcloneProviderExample> Examples { get; set; } = [];
}

public sealed class RcloneProviderExample
{
    public string Value { get; set; } = "";
    public string Help { get; set; } = "";
    public string Provider { get; set; } = "";
}

public sealed class ProviderConfigurationStep
{
    public string State { get; set; } = "";
    public string Error { get; set; } = "";
    public RcloneProviderOption? Option { get; set; }
    public bool Complete => string.IsNullOrEmpty(State) && Option is null && string.IsNullOrEmpty(Error);
}

public sealed partial class RcloneService
{
    public async Task<IReadOnlyList<RcloneProvider>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["config", "providers"], cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Cancelled ? "读取存储提供商已取消。" : "无法读取 rclone 存储提供商目录。");
        var providers = JsonSerializer.Deserialize<List<RcloneProvider>>(result.Output, SettingsStore.JsonOptions) ?? [];
        foreach (var provider in providers)
        {
            provider.Options ??= [];
            foreach (var option in provider.Options) { option.Examples ??= []; option.Provider ??= ""; option.DefaultStr ??= ""; }
        }
        return providers.OrderBy(p => p.Description, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ProviderConfigurationSession> BeginProviderConfigurationAsync(string name, string provider, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default, bool openBrowser = true)
    {
        ValidateRemoteName(name);
        ArgumentNullException.ThrowIfNull(parameters);
        var metadata = (await GetProvidersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.Name == provider) ?? throw new ArgumentException("当前 rclone 不支持所选存储提供商。");
        var known = metadata.Options.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (!known.Contains(key)) throw new ArgumentException($"存储提供商不支持参数：{key}");
            if (value is null || value.Contains('\0') || value.Length > 1024 * 1024) throw new ArgumentException($"参数 {key} 的值无效或过长。");
        }
        var selectedProvider = parameters.GetValueOrDefault("provider") ?? metadata.Options.FirstOrDefault(o => o.Name == "provider")?.DefaultText ?? "";
        foreach (var option in metadata.Options.Where(o => o.Required && (o.Hide & 2) == 0 && MatchesProvider(o.Provider, selectedProvider)))
            if (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault(option.Name) ?? option.DefaultText)) throw new ArgumentException($"请填写必需参数：{option.Name}");
        if ((await ListRemotesAsync(cancellationToken).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("同名远程已存在，请选择其他名称。");
        var session = new ProviderConfigurationSession(Settings, name, provider, parameters, openBrowser);
        try { await session.InitializeAsync(cancellationToken).ConfigureAwait(false); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static bool MatchesProvider(string? condition, string selected)
    {
        if (string.IsNullOrEmpty(condition) || selected.Length == 0) return true;
        var negative = condition.StartsWith('!');
        var values = (negative ? condition[1..] : condition).Split(',');
        return values.Contains(selected, StringComparer.Ordinal) != negative;
    }
}

public sealed class ProviderConfigurationSession : IAsyncDisposable
{
    private readonly string _configPath;
    private readonly string _temporaryDirectory;
    private readonly string _temporaryConfig;
    private readonly byte[]? _originalHash;
    private readonly Dictionary<string, string> _parameters;
    private readonly RcloneService _temporaryService;
    private readonly bool _openBrowser;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private RcloneService.RcSession? _rc;
    private int _disposed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    public string Name { get; }
    public string Provider { get; }
    public ProviderConfigurationStep CurrentStep { get; private set; } = new() { State = "initializing" };
    public bool IsCommitted { get; private set; }
    public string? AuthorizationUrl { get; private set; }
    public event EventHandler<string>? AuthorizationUrlAvailable;

    internal ProviderConfigurationSession(AppSettings settings, string name, string provider, IReadOnlyDictionary<string, string> parameters, bool openBrowser)
    {
        if (string.IsNullOrWhiteSpace(settings.ConfigPath)) throw new ArgumentException("请先选择 rclone 配置文件路径。");
        Name = name; Provider = provider;
        _openBrowser = openBrowser;
        _configPath = Path.GetFullPath(settings.ConfigPath);
        if (File.Exists(_configPath))
        {
            var original = File.ReadAllBytes(_configPath);
            EnsureUnencrypted(original);
            if (HasSection(Decode(original), name)) throw new ArgumentException("同名远程已存在，请选择其他名称。");
            _originalHash = SHA256.HashData(original);
        }
        _parameters = new(parameters, StringComparer.Ordinal);
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "RcloneLink-Provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        _temporaryConfig = Path.Combine(_temporaryDirectory, "draft.conf");
        File.WriteAllText(_temporaryConfig, "", new UTF8Encoding(false));
        _temporaryService = new(new AppSettings { RclonePath = settings.RclonePath, ConfigPath = _temporaryConfig });
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string>? environment = _openBrowser ? null : new() { ["RCLONE_CONFIG_" + (Name + "_config_auth_no_browser").ToUpperInvariant()] = "1" };
        _rc = _temporaryService.StartRcProcess(["rcd", "--log-level", "NOTICE"], new AuthorizationProgress(ObserveAuthorizationLine), Timeout.InfiniteTimeSpan, environment);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        readiness.CancelAfter(TimeSpan.FromSeconds(10));
        await _rc.WaitReadyAsync(readiness.Token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CurrentStep = await RequestStepAsync("config/create", new { name = Name, type = Provider, parameters = _parameters, opt = new { nonInteractive = true, obscure = true } }, linked.Token).ConfigureAwait(false);
    }

    public async Task<ProviderConfigurationStep> AdvanceAsync(string result, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("配置步骤仍在进行，请等待当前步骤或取消向导。");
        try
        {
            if (IsCommitted || CurrentStep.Complete) throw new InvalidOperationException("配置步骤已完成。");
            if (result is null || result.Contains('\0') || result.Length > 1024 * 1024) throw new ArgumentException("配置输入无效或过长。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var previousOption = CurrentStep.Option;
            var next = await RequestStepAsync("config/update", new { name = Name, parameters = _parameters, opt = new { nonInteractive = true, obscure = true, @continue = true, state = CurrentStep.State, result } }, linked.Token).ConfigureAwait(false);
            if (next.Error.Length > 0 && next.Option is null) next.Option = previousOption;
            CurrentStep = next;
            return CurrentStep;
        }
        catch (OperationCanceledException) { _lifetime.Cancel(); throw; }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { throw new OperationCanceledException("配置已取消。"); }
        finally
        {
            _operation.Release();
            if (_lifetime.IsCancellationRequested) await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("配置步骤仍在进行，暂时不能保存。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        try
        {
            if (IsCommitted) return;
            if (!CurrentStep.Complete) throw new InvalidOperationException("请先完成存储提供商的全部配置步骤。");
            cancellationToken.ThrowIfCancellationRequested();
            // Stop the isolated server before reading its finalized config.
            if (_rc is not null) { await _rc.DisposeAsync().ConfigureAwait(false); _rc = null; }
            var draft = Decode(await File.ReadAllBytesAsync(_temporaryConfig, cancellationToken).ConfigureAwait(false));
            var section = ExtractSection(draft, Name) ?? throw new InvalidDataException("rclone 没有生成完整的目标远程配置。");
            if (!Regex.IsMatch(section, @"(?m)^type\s*=\s*" + Regex.Escape(Provider) + @"\s*$")) throw new InvalidDataException("生成的存储类型与所选提供商不一致。");
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var lockPath = _configPath + ".rclonelink-write.lock";
            FileStream writeLock;
            try { writeLock = new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { throw new InvalidOperationException("另一个 Cloudlet 配置操作正在写入，请稍后重试。"); }
            await using (writeLock)
            {
                var exists = File.Exists(_configPath);
                var original = exists ? await File.ReadAllBytesAsync(_configPath, cancellationToken).ConfigureAwait(false) : [];
                EnsureUnencrypted(original);
                if (exists != (_originalHash is not null) || exists && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), _originalHash!)) throw new InvalidOperationException("配置文件在向导打开后已发生变化。为保留外部修改，请取消并重新开始。");
                if (HasSection(Decode(original), Name)) throw new InvalidOperationException("同名远程已被其他操作创建，未覆盖现有配置。");
                var suffix = Encoding.UTF8.GetBytes((original.Length == 0 ? "" : "\r\n") + section.TrimEnd() + "\r\n");
                var merged = new byte[original.Length + suffix.Length];
                original.CopyTo(merged, 0); suffix.CopyTo(merged, original.Length);
                var staged = _configPath + ".rclonelink-draft-" + Guid.NewGuid().ToString("N");
                try
                {
                    await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    { await stream.WriteAsync(merged, cancellationToken).ConfigureAwait(false); stream.Flush(true); }
                    cancellationToken.ThrowIfCancellationRequested();
                    // A second compare narrows interference from external config editors while staging.
                    var latest = File.Exists(_configPath) ? File.ReadAllBytes(_configPath) : null;
                    if ((latest is not null) != exists || latest is not null && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(latest), _originalHash!)) throw new InvalidOperationException("保存期间配置文件发生变化，已停止保存，请重新开始。");
                    if (exists) File.Copy(_configPath, _configPath + ".rclonelink-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(staged, _configPath, overwrite: exists);
                    IsCommitted = true;
                    _parameters.Clear();
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }
        finally { _operation.Release(); }
    }

    private async Task<ProviderConfigurationStep> RequestStepAsync(string endpoint, object body, CancellationToken cancellationToken)
    {
        using var response = await _rc!.PostAsync(endpoint, body, cancellationToken).ConfigureAwait(false);
        // RC error JSON echoes the complete input (including passwords), so never expose its body.
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"存储配置步骤失败（HTTP {(int)response.StatusCode}）。请检查字段后重新开始；含凭据的诊断已隐藏。");
        var step = await JsonSerializer.DeserializeAsync<ProviderConfigurationStep>(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), SettingsStore.JsonOptions, cancellationToken).ConfigureAwait(false) ?? new();
        step.State ??= "";
        if (!string.IsNullOrEmpty(step.Error)) step.Error = "rclone 未接受此步骤的输入，请检查格式或重新选择。";
        if (step.Option is { } option)
        {
            option.Examples ??= [];
            option.Provider ??= "";
            if (Regex.IsMatch(option.Name, "token|password|secret", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) option.Sensitive = true;
            if (option.IsPassword || option.Sensitive) { option.Default = default; option.DefaultStr = ""; }
        }
        return step;
    }

    private void ObserveAuthorizationLine(string line)
    {
        var match = Regex.Match(line, @"http://127\.0\.0\.1:\d+/auth\?state=[A-Za-z0-9_~.%-]+", RegexOptions.CultureInvariant);
        if (!match.Success || !Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || !uri.IsLoopback) return;
        AuthorizationUrl = uri.AbsoluteUri;
        AuthorizationUrlAvailable?.Invoke(this, AuthorizationUrl);
    }

    private static string Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    private static void EnsureUnencrypted(byte[] bytes)
    { if (Decode(bytes).Contains("RCLONE_ENCRYPT_V", StringComparison.Ordinal)) throw new InvalidOperationException("当前 rclone 配置已加密，暂不能安全合并。请在设置中选择独立的未加密配置文件后添加连接。"); }
    private static readonly Regex SectionHeader = new(@"(?m)^[ \t]*\[(?<name>[^\r\n\]]+)\][ \t]*\r?$", RegexOptions.Compiled);
    private static bool HasSection(string text, string name) => SectionHeader.Matches(text).Any(m => m.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static string? ExtractSection(string text, string name)
    {
        var headers = SectionHeader.Matches(text);
        for (var i = 0; i < headers.Count; i++)
            if (headers[i].Groups["name"].Value.Equals(name, StringComparison.Ordinal)) return text[headers[i].Index..(i + 1 < headers.Count ? headers[i + 1].Index : text.Length)];
        return null;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_rc is not null) { await _rc.DisposeAsync().ConfigureAwait(false); _rc = null; }
            _parameters.Clear();
            if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
        }
        finally { _operation.Release(); }
    }
    private sealed class AuthorizationProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
