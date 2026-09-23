using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RcloneLink.Core;

public sealed record DependencyRelease(Version Version, string DownloadUrl = "", string Sha256 = "");

public sealed class DependencyUpdates
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Cloudlet/2.0");
        return client;
    }

    public static Version ParseVersion(string value)
    {
        var match = Regex.Match(value, @"(?<!\d)(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) throw new FormatException("无法识别版本号。");
        return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0,
            match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0);
    }

    public async Task<DependencyRelease> CheckRcloneAsync(CancellationToken token = default) =>
        new(ParseVersion(await Client.GetStringAsync("https://downloads.rclone.org/version.txt", token)));

    public async Task<DependencyRelease> CheckWinFspAsync(CancellationToken token = default)
    {
        using var json = JsonDocument.Parse(await Client.GetStringAsync("https://api.github.com/repos/winfsp/winfsp/releases/latest", token));
        if (json.RootElement.GetProperty("prerelease").GetBoolean() || json.RootElement.GetProperty("draft").GetBoolean())
            throw new InvalidDataException("WinFsp 尚无可用的正式版本。");
        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, @"^winfsp-\d+\.\d+\.\d+\.msi$")) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith("https://github.com/winfsp/winfsp/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("WinFsp 下载地址不属于官方发布源。");
            var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
            if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("官方未提供 WinFsp SHA256，无法验证安装包。");
            return new(ParseVersion(name), url, digest[7..]);
        }
        throw new InvalidDataException("官方发布中没有 WinFsp 安装包。");
    }

    public async Task<string> DownloadWinFspAsync(DependencyRelease release, string directory, CancellationToken token)
    {
        if (!release.DownloadUrl.StartsWith("https://github.com/winfsp/winfsp/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("无效的 WinFsp 下载源。");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "winfsp-" + release.Version.ToString(3) + ".msi");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(temporary)) await response.Content.CopyToAsync(file, token);
            await using (var file = File.OpenRead(temporary))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("WinFsp 安装包校验失败。");
            }
            File.Move(temporary, path, true);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> StageRcloneAsync(RcloneService current, DependencyRelease release, string directory, CancellationToken token)
    {
        if (ParseVersion(await current.GetVersionAsync(token)) >= release.Version) return current.Settings.RclonePath;
        // Never replace the selected engine in place: it may be shared or in Program Files.
        var target = Path.Combine(directory, "rclone-" + release.Version.ToString(3), Guid.NewGuid().ToString("N"), "rclone.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            var result = await current.RunAsync(["selfupdate", "--stable", "--version", release.Version.ToString(3), "--output", target], token);
            if (!result.Success) throw new InvalidOperationException("rclone 更新失败：" + result.Error);
            var probe = new RcloneService(new AppSettings { RclonePath = target, ConfigPath = current.Settings.ConfigPath });
            if (ParseVersion(await probe.GetVersionAsync(token)) != release.Version) throw new InvalidDataException("下载的 rclone 版本不匹配。");
            return target;
        }
        catch { if (File.Exists(target)) File.Delete(target); throw; }
    }
}
