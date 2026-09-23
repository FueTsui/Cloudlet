using System.Text;
using System.Text.Json;

namespace RcloneLink.Core;

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _root;
    private readonly bool _migrateLegacy;
    public string StatePath => Path.Combine(_root, "state.json");

    public SettingsStore(string? rootDirectory = null)
    {
        _migrateLegacy = rootDirectory is null;
        _root = Path.GetFullPath(rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RcloneLink", "v2"));
    }

    public AppState Load()
    {
        if (File.Exists(StatePath))
        {
            try { return Normalize(JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? throw new JsonException("配置为空。")); }
            catch (JsonException ex) { throw new InvalidDataException($"应用配置格式损坏，原文件已保留：{StatePath}", ex); }
        }
        var state = new AppState();
        if (_migrateLegacy)
        {
            var legacyPath = Path.Combine(Directory.GetParent(_root)!.FullName, "settings.json");
            if (File.Exists(legacyPath))
            {
                state = ImportLegacySettings(legacyPath);
                Save(state);
            }
        }
        return state;
    }

    public void Save(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_root);
        AtomicWrite(StatePath, JsonSerializer.Serialize(Normalize(state), JsonOptions));
    }

    public AppState ImportLegacySettings(string settingsPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("旧版设置必须是 JSON 对象。");
        var state = new AppState();
        var settings = state.Settings;
        if (ReadString(root, "rclone_path") is { Length: > 0 } executable)
            settings.RclonePath = Path.IsPathFullyQualified(executable) ? executable : Path.GetFullPath(executable, Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
        if (root.TryGetProperty("tray_icon_hidden", out var hidden) && hidden.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.TrayVisible = !hidden.GetBoolean();
        settings.Theme = ReadString(root, "theme") switch { "浅色" or "Light" => "Light", "深色" or "Dark" => "Dark", _ => "System" };
        if (root.TryGetProperty("default_mount_options", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
            foreach (var item in defaults.EnumerateObject()) settings.DefaultMountOptions[item.Name] = Scalar(item.Value);
        if (ReadString(root, "last_mount_list") is { Length: > 0 } mountPath)
        {
            if (!Path.IsPathFullyQualified(mountPath)) mountPath = Path.GetFullPath(mountPath, Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            if (File.Exists(mountPath)) state.Mounts = ImportMounts(mountPath);
        }
        return Normalize(state);
    }

    public List<MountProfile> ImportMounts(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryProperty(root, "Mounts", out var mounts)) root = mounts;
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("挂载列表应为 JSON 数组，或包含 Mounts 的应用配置。");
        List<MountProfile> result = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("挂载列表包含无效项目。");
            MountProfile profile;
            if (item.TryGetProperty("remote_name", out var legacyRemote))
            {
                profile = new() { Name = legacyRemote.GetString() ?? "", Remote = legacyRemote.GetString() ?? "", MountPoint = ReadString(item, "mount_point") ?? "" };
                foreach (var property in item.EnumerateObject())
                    if (property.Name is not ("remote_name" or "mount_point")) profile.Options[property.Name] = Scalar(property.Value);
            }
            else profile = JsonSerializer.Deserialize<MountProfile>(item, JsonOptions) ?? throw new InvalidDataException("挂载项目为空。");
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = profile.Remote;
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id)) { profile.Id = Guid.NewGuid().ToString("N"); ids.Add(profile.Id); }
            CommandBuilder.ValidateMount(profile);
            result.Add(profile);
        }
        return result;
    }

    public void ExportMounts(string jsonPath, IEnumerable<MountProfile> mounts)
    {
        var path = Path.GetFullPath(jsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, JsonSerializer.Serialize(mounts.ToList(), JsonOptions));
    }

    private static AppState Normalize(AppState state)
    {
        if (state.Settings is null || state.Mounts is null || state.Mounts.Any(m => m is null || m.Options is null)) throw new InvalidDataException("应用配置包含空的设置或挂载项目，原文件已保留。");
        if (state.Settings.DefaultMountOptions is null) state.Settings.DefaultMountOptions = CommandBuilder.DefaultMountOptions();
        if (state.Settings.Theme is not ("System" or "Light" or "Dark")) state.Settings.Theme = "System";
        return state;
    }

    private static void AtomicWrite(string destination, string content)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Scalar(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => value.ToString(), _ => throw new InvalidDataException("挂载参数应为字符串、数字或布尔值。") };
    private static bool TryProperty(JsonElement element, string name, out JsonElement result)
    {
        foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { result = property.Value; return true; }
        result = default; return false;
    }
}
