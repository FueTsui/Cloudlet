using System.Globalization;
using System.Text.RegularExpressions;

namespace RcloneLink.Core;

public static class CommandBuilder
{
    private static readonly HashSet<string> BooleanMountOptions = ["progress", "no_check_certificate", "links", "network_mode", "async_read", "ignore_case", "read_only"];
    private static readonly HashSet<string> ValueMountOptions = ["vfs_cache_mode", "vfs_read_ahead", "buffer_size", "cache_dir", "log_level", "file_perms", "dir_cache_time", "vfs_cache_max_age", "vfs_cache_poll_interval", "vfs_cache_max_size", "vfs_disk_space_total_size", "vfs_read_chunk_size", "vfs_read_chunk_size_limit", "transfers", "multi_thread_streams", "timeout", "contimeout", "retries", "retries_sleep", "bwlimit"];
    private static readonly HashSet<string> SizeOptions = ["vfs_read_ahead", "buffer_size", "vfs_cache_max_size", "vfs_disk_space_total_size", "vfs_read_chunk_size", "vfs_read_chunk_size_limit", "bwlimit"];
    private static readonly HashSet<string> DurationOptions = ["dir_cache_time", "vfs_cache_max_age", "vfs_cache_poll_interval", "timeout", "contimeout", "retries_sleep"];
    private static readonly HashSet<string> IntegerOptions = ["transfers", "multi_thread_streams", "retries"];

    public static Dictionary<string, string> DefaultMountOptions() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["cache_dir"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneLink", "cache"),
        ["log_level"] = "INFO", ["network_mode"] = "false", ["no_check_certificate"] = "false", ["async_read"] = "true", ["ignore_case"] = "true", ["progress"] = "false", ["links"] = "true",
        ["vfs_cache_mode"] = "full", ["vfs_cache_max_size"] = "20G", ["vfs_cache_max_age"] = "1h", ["vfs_cache_poll_interval"] = "10s", ["dir_cache_time"] = "1m",
        ["vfs_read_ahead"] = "512M", ["vfs_read_chunk_size"] = "128M", ["vfs_read_chunk_size_limit"] = "2G", ["buffer_size"] = "128M", ["transfers"] = "8", ["multi_thread_streams"] = "12",
        ["timeout"] = "1m", ["contimeout"] = "15s", ["retries"] = "5", ["retries_sleep"] = "10s", ["vfs_disk_space_total_size"] = "8T", ["file_perms"] = "0777"
    };

    public static string NormalizeRemote(string remote)
    {
        ValidateText(remote, "远程路径");
        remote = remote.Trim();
        if (Path.IsPathRooted(remote) || remote.StartsWith('.') || remote.Contains(':') || remote.Contains('/') || remote.Contains('\\')) return remote;
        return remote + ":";
    }

    public static void ValidateMount(MountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateText(profile.Id, "挂载标识");
        ValidateText(profile.Remote, "远程路径");
        ValidateText(profile.MountPoint, "挂载点");
        if (!Regex.IsMatch(profile.MountPoint, @"^[A-Za-z]:[\\/]?$") && !Path.IsPathFullyQualified(profile.MountPoint))
            throw new ArgumentException("挂载点必须是盘符（例如 X:）或绝对目录路径。");
        foreach (var (key, raw) in profile.Options)
        {
            if (key is "remote_name" or "mount_point") continue;
            var value = raw?.Trim() ?? "";
            if (!BooleanMountOptions.Contains(key) && !ValueMountOptions.Contains(key)) throw new ArgumentException($"不支持的挂载参数：{key}");
            if (value.Length == 0) continue;
            if (value.Any(c => c is '\0' or '\r' or '\n')) throw new ArgumentException($"参数 {key} 不能包含换行或空字符。");
            if (BooleanMountOptions.Contains(key) && !bool.TryParse(value, out _)) throw new ArgumentException($"参数 {key} 应为 true 或 false。");
            if (SizeOptions.Contains(key) && !Regex.IsMatch(value, @"^(off|[0-9]+(?:\.[0-9]+)?(?:[kKMGTPE]i?[bB]?)?)$")) throw new ArgumentException($"参数 {key} 应为大小，例如 128M、20G 或 off。");
            if (DurationOptions.Contains(key) && !Regex.IsMatch(value, @"^(off|0|(?:\d+(?:\.\d+)?(?:ns|us|µs|ms|s|m|h|d|w|M|y))+)$")) throw new ArgumentException($"参数 {key} 应为时间，例如 30s 或 1h。");
            if (IntegerOptions.Contains(key) && (!int.TryParse(value, out var number) || number < (key == "multi_thread_streams" ? 0 : 1) || number > 1024)) throw new ArgumentException($"参数 {key} 的整数值超出允许范围。");
            if (key == "vfs_cache_mode" && value is not ("off" or "minimal" or "writes" or "full")) throw new ArgumentException("VFS 缓存模式应为 off、minimal、writes 或 full。");
            if (key == "file_perms" && !Regex.IsMatch(value, @"^0?[0-7]{3}$")) throw new ArgumentException("文件权限应为八进制值，例如 0777。");
            if (key == "log_level" && value is not ("DEBUG" or "INFO" or "NOTICE" or "WARNING" or "ERROR" or "CRITICAL")) throw new ArgumentException("日志级别无效。");
        }
    }

    public static IReadOnlyList<string> BuildMount(MountProfile profile)
    {
        ValidateMount(profile);
        List<string> args = ["mount", NormalizeRemote(profile.Remote), profile.MountPoint];
        var options = DefaultMountOptions();
        foreach (var pair in profile.Options) options[pair.Key] = pair.Value;
        foreach (var (key, value) in options)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (BooleanMountOptions.Contains(key)) args.Add("--" + key.Replace('_', '-') + "=" + value.ToLowerInvariant());
            else if (ValueMountOptions.Contains(key)) { args.Add("--" + key.Replace('_', '-')); args.Add(value); }
        }
        if (OperatingSystem.IsWindows()) args.Add("--no-console");
        return args;
    }

    public static IReadOnlyList<string> BuildTransfer(TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Source, "源路径"); ValidateText(request.Destination, "目标路径");
        if (!Enum.IsDefined(request.Operation)) throw new ArgumentException("传输操作无效。");
        if (request.Transfers is < 1 or > 128 || request.Checkers is < 1 or > 256) throw new ArgumentException("并发传输数应为 1–128，检查数应为 1–256。");
        if (request.Checksum && request.SizeOnly) throw new ArgumentException("校验和与仅比较大小不能同时启用。");
        if (AreSamePath(request.Source, request.Destination)) throw new ArgumentException("源路径和目标路径不能相同。");
        if (request.Operation != TransferOperation.Check && AreNestedLocalPaths(request.Source, request.Destination)) throw new ArgumentException("本地源目录和目标目录不能相互包含，避免递归复制或误删除。");
        if (request.Operation != TransferOperation.Check && AreOverlappingRemotePaths(request.Source, request.Destination)) throw new ArgumentException("同一远程的源目录和目标目录不能相同或相互包含。");
        List<string> args = [request.Operation.ToString().ToLowerInvariant(), request.Source, request.Destination, "--transfers", request.Transfers.ToString(CultureInfo.InvariantCulture), "--checkers", request.Checkers.ToString(CultureInfo.InvariantCulture), "--stats", "1s", "--stats-one-line", "--log-level", "INFO"];
        if (request.DryRun) args.Add("--dry-run");
        if (request.Checksum) args.Add("--checksum");
        if (request.SizeOnly) args.Add("--size-only");
        if (request.IgnoreExisting) args.Add("--ignore-existing");
        if (!string.IsNullOrWhiteSpace(request.BandwidthLimit))
        {
            if (!Regex.IsMatch(request.BandwidthLimit, @"^(off|[0-9]+(?:\.[0-9]+)?[kKMGTPE]?)(:(off|[0-9]+(?:\.[0-9]+)?[kKMGTPE]?))?$")) throw new ArgumentException("限速格式应为 10M、1G 或 off。");
            args.AddRange(["--bwlimit", request.BandwidthLimit]);
        }
        foreach (var include in request.Includes.Where(x => !string.IsNullOrWhiteSpace(x))) { ValidateText(include, "包含规则"); args.AddRange(["--include", include]); }
        foreach (var exclude in request.Excludes.Where(x => !string.IsNullOrWhiteSpace(x))) { ValidateText(exclude, "排除规则"); args.AddRange(["--exclude", exclude]); }
        return args;
    }

    private static bool AreSamePath(string a, string b)
    {
        if (IsLocalPath(a) && IsLocalPath(b)) return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return a.TrimEnd('/') == b.TrimEnd('/');
    }

    private static bool AreNestedLocalPaths(string a, string b)
    {
        if (!IsLocalPath(a) || !IsLocalPath(b)) return false;
        var first = Path.GetFullPath(a).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var second = Path.GetFullPath(b).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return first.StartsWith(second, comparison) || second.StartsWith(first, comparison);
    }

    private static bool IsLocalPath(string value) => !value.Contains(':') || OperatingSystem.IsWindows() && Regex.IsMatch(value, @"^[A-Za-z]:") || value.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool AreOverlappingRemotePaths(string a, string b)
    {
        if (IsLocalPath(a) || IsLocalPath(b) || a.StartsWith(':') || b.StartsWith(':')) return false;
        var aColon = a.IndexOf(':'); var bColon = b.IndexOf(':');
        if (!a[..aColon].Equals(b[..bColon], StringComparison.OrdinalIgnoreCase)) return false;
        static string NormalizePath(string path)
        {
            List<string> parts = [];
            foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                else parts.Add(part);
            }
            return string.Join('/', parts);
        }
        var first = NormalizePath(a[(aColon + 1)..]); var second = NormalizePath(b[(bColon + 1)..]);
        return first == second || first.Length == 0 || second.Length == 0 || first.StartsWith(second + "/", StringComparison.Ordinal) || second.StartsWith(first + "/", StringComparison.Ordinal);
    }

    internal static void ValidateText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c is '\0' or '\r' or '\n')) throw new ArgumentException($"{label}不能为空，也不能包含换行或空字符。");
        if (value.StartsWith('-')) throw new ArgumentException($"{label}不能以减号开头；本地相对路径请加 ./ 前缀。");
    }
}
