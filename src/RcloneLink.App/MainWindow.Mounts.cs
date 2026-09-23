using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private readonly StackPanel _mountRows = new() { Spacing = 10 };
    private readonly TextBlock _mountSummary = Hint("");

    private FrameworkElement BuildMountsPage()
    {
        var page = Page("磁盘挂载", "把云端目录映射为 Windows 驱动器，保留熟悉的资源管理器体验。", out var body);
        body.Children.Add(Horizontal(
            ActionButton("AddMount", "添加挂载", () => EditMountAsync(null), "\uE710", true),
            ActionButton("StartAllMounts", "全部启动", StartAllMountsAsync, "\uE768"),
            ActionButton("StopAllMounts", "全部停止", async () =>
            {
                await _mounts.StopAllAsync(_lifetime.Token);
                Notify("已停止当前应用管理的所有挂载。");
            }, "\uE71A")));
        body.Children.Add(Horizontal(
            ActionButton("ImportMounts", "导入配置", ImportMountsAsync, "\uE8B5"),
            ActionButton("ExportMounts", "导出配置", ExportMountsAsync, "\uE74E"),
            ActionButton("DefaultMountOptions", "默认挂载参数", () => EditMountAsync(null, true), "\uE713")));
        body.Children.Add(_mountSummary);
        body.Children.Add(_mountRows);
        body.Children.Add(Hint("挂载需要 WinFsp。停止挂载只处理由本应用启动的进程；停止前请先关闭打开的文件，并等待写入完成。"));
        return page;
    }

    private void RefreshMountRows()
    {
        _mountRows.Children.Clear();
        _mountSummary.Text = $"共 {_state.Mounts.Count} 个挂载配置 · {_mounts.GetStates().Count(s => s.Status == MountStatus.Mounted)} 个正在运行";
        if (_state.Mounts.Count == 0)
        {
            _mountRows.Children.Add(Card(Stack(new FontIcon { Glyph = "\uEDA2", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Left },
                Label("添加你的第一个云端磁盘", 20, true), Label("选择远程目录和空闲盘符，再按需调整缓存与读写参数。已有旧版 JSON 配置也可以直接导入。"))));
            return;
        }
        foreach (var profile in _state.Mounts)
        {
            var selected = profile;
            var status = _mounts.GetState(profile.Id);
            var active = status.Status is MountStatus.Starting or MountStatus.Mounted or MountStatus.Stopping;
            var grid = new Grid { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = Stack(Label(string.IsNullOrWhiteSpace(profile.Name) ? profile.Remote : profile.Name, 16, true),
                Label($"{profile.Remote}  →  {profile.MountPoint}"),
                Hint(MountStatusText(status.Status) + (string.IsNullOrWhiteSpace(status.Message) ? "" : " · " + status.Message)));
            text.Spacing = 5;
            grid.Children.Add(text);
            var actions = Horizontal(
                ActionButton("ToggleMount" + SafeName(profile.Id), active ? "停止" : "启动", async () =>
                {
                    if (active) await _mounts.StopAsync(selected.Id, _lifetime.Token);
                    else await StartMountAsync(selected);
                }, active ? "\uE71A" : "\uE768", !active),
                SimpleButton("OpenMount" + SafeName(profile.Id), "打开", () =>
                {
                    if (_mounts.GetState(selected.Id).Status != MountStatus.Mounted) throw new InvalidOperationException("请先启动挂载，确认磁盘可用后再打开。");
                    NativeIntegration.OpenPath(selected.MountPoint.EndsWith(':') ? selected.MountPoint + "\\" : selected.MountPoint);
                }),
                ActionButton("EditMount" + SafeName(profile.Id), "编辑", () => EditMountAsync(selected)),
                ActionButton("DeleteMount" + SafeName(profile.Id), "删除", () => DeleteMountAsync(selected)));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            _mountRows.Children.Add(Card(grid));
        }
    }

    private static string MountStatusText(MountStatus status) => status switch
    {
        MountStatus.Starting => "正在启动", MountStatus.Mounted => "已挂载", MountStatus.Stopping => "正在停止", MountStatus.Failed => "挂载失败", _ => "未挂载"
    };

    private async Task StartMountAsync(MountProfile profile)
    {
        if (_exitPending) return;
        if (!NativeIntegration.IsWinFspInstalled()) throw new InvalidOperationException("未检测到 WinFsp。请在“概览”中安装 WinFsp，然后重新尝试挂载。");
        var result = await _mounts.StartAsync(profile, _lifetime.Token);
        if (result.Status != MountStatus.Mounted) throw new InvalidOperationException(result.Message);
        Notify($"{profile.Name} 已挂载到 {profile.MountPoint}。");
    }

    private async Task StartAllMountsAsync()
    {
        if (_state.Mounts.Count == 0) { Notify("请先添加挂载配置。", InfoBarSeverity.Informational); return; }
        if (!NativeIntegration.IsWinFspInstalled()) throw new InvalidOperationException("未检测到 WinFsp。请在“概览”中安装后再启动挂载。");
        var failures = new List<string>();
        var started = 0;
        foreach (var mount in _state.Mounts.ToArray())
        {
            if (_exitPending) break;
            if (_mounts.GetState(mount.Id).Status is MountStatus.Mounted or MountStatus.Starting) continue;
            try
            {
                var result = await _mounts.StartAsync(mount, _lifetime.Token);
                if (result.Status == MountStatus.Mounted) started++;
                else failures.Add(mount.Name + "：" + result.Message);
            }
            catch (Exception ex) { failures.Add(mount.Name + "：" + ex.Message); }
        }
        Notify(failures.Count == 0 ? $"已启动 {started} 个挂载。" : $"启动 {started} 个，失败 {failures.Count} 个。\n" + string.Join("\n", failures), failures.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task DeleteMountAsync(MountProfile profile)
    {
        if (_mounts.GetState(profile.Id).Status is MountStatus.Mounted or MountStatus.Starting or MountStatus.Stopping)
            throw new InvalidOperationException("该挂载正在运行。请先停止挂载，再删除配置。");
        if (!await ConfirmAsync("删除挂载配置？", $"将删除“{profile.Name}”的挂载配置。远程连接和云端文件不会被删除。", "删除配置")) return;
        _state.Mounts.Remove(profile);
        SaveState();
        RefreshMountRows();
        RefreshOverviewCounts();
        Notify("挂载配置已删除。");
    }

    private async Task ImportMountsAsync()
    {
        var path = await NativeIntegration.PickFileAsync(this, "导入挂载配置", ".json");
        if (path == null) return;
        var incoming = _store.ImportMounts(path);
        foreach (var profile in incoming) CommandBuilder.ValidateMount(profile);
        var added = 0;
        var skipped = 0;
        foreach (var profile in incoming)
        {
            if (_state.Mounts.Any(m => m.MountPoint.Equals(profile.MountPoint, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
            profile.Id = Guid.NewGuid().ToString("N");
            _state.Mounts.Add(profile);
            added++;
        }
        SaveState();
        RefreshMountRows();
        RefreshOverviewCounts();
        Notify($"已导入 {added} 个挂载配置" + (skipped > 0 ? $"；跳过 {skipped} 个与现有挂载点重复的配置。" : "。") + " 导入不会自动启动挂载。");
    }

    private async Task ExportMountsAsync()
    {
        var path = await NativeIntegration.SaveFileAsync(this, "导出挂载配置", "Cloudlet-mounts.json", ".json");
        if (path == null) return;
        _store.ExportMounts(path, _state.Mounts);
        Notify($"已导出 {_state.Mounts.Count} 个挂载配置。导出文件不包含远程密码。");
    }

    private async Task EditMountAsync(MountProfile? existing, bool defaultsOnly = false)
    {
        if (existing != null && _mounts.GetState(existing.Id).Status is MountStatus.Mounted or MountStatus.Starting or MountStatus.Stopping)
            throw new InvalidOperationException("请先停止此挂载，再修改配置。");
        if (!defaultsOnly && _remotes.Count == 0) await RefreshRemotesAsync();
        var values = CommandBuilder.DefaultMountOptions();
        foreach (var pair in _state.Settings.DefaultMountOptions) values[pair.Key] = pair.Value;
        if (existing != null) foreach (var pair in existing.Options) values[pair.Key] = pair.Value;
        var name = TextInput("MountName", "显示名称", existing?.Name ?? "");
        var remote = TextInput("MountRemotePath", "远程路径", existing?.Remote ?? (_remotes.FirstOrDefault() is string first ? first + ":" : ""), "远程名称:目录");
        var mountPoint = TextInput("MountPoint", "Windows 盘符或空文件夹", existing?.MountPoint ?? NextDrive(), "例如 X: 或 C:\\CloudMount");
        var auto = Toggle("MountAutoStart", "随应用自动挂载", existing?.AutoMount ?? true);
        var form = new StackPanel { Spacing = 16, MinWidth = 550 };
        if (!defaultsOnly)
        {
            form.Children.Add(name);
            form.Children.Add(remote);
            if (_remotes.Count > 0)
            {
                var picker = SelectInput("MountRemotePicker", "从现有连接填入", _remotes, existing?.Remote.Split(':')[0]);
                picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is string chosen) remote.Text = chosen + ":"; };
                form.Children.Add(picker);
            }
            var pointGrid = new Grid { ColumnSpacing = 8 };
            pointGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pointGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pointGrid.Children.Add(mountPoint);
            var selectFolder = ActionButton("PickMountFolder", "选择文件夹", async () => { var folder = await NativeIntegration.PickFolderAsync(this); if (folder != null) mountPoint.Text = folder; });
            Grid.SetColumn(selectFolder, 1);
            pointGrid.Children.Add(selectFolder);
            form.Children.Add(pointGrid);
            form.Children.Add(auto);
            form.Children.Add(Hint("全局“启动后自动挂载”和此选项均开启时，应用启动后才会自动挂载。"));
        }
        else form.Children.Add(Hint("保存后用于新建挂载，已有挂载配置不受影响。空值代表省略对应参数，使用 rclone 默认值。"));
        var editors = BuildMountOptionEditors(values, form);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false };
        form.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = defaultsOnly ? "默认挂载参数" : existing == null ? "添加磁盘挂载" : "编辑磁盘挂载",
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer { Content = form, MaxHeight = 580, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            RequestedTheme = _root.RequestedTheme
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var options = editors.ToDictionary(p => p.Key, p => p.Value switch { TextBox t => t.Text.Trim(), ComboBox c => c.SelectedItem?.ToString() ?? "", ToggleSwitch s => s.IsOn ? "true" : "false", _ => "" });
                if (defaultsOnly)
                {
                    CommandBuilder.ValidateMount(new MountProfile { Id = "defaults", Name = "默认参数", Remote = "default:", MountPoint = "Z:", Options = options });
                    _state.Settings.DefaultMountOptions = options;
                    SaveState();
                    return;
                }
                var profile = new MountProfile
                {
                    Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(name.Text) ? remote.Text.Trim() : name.Text.Trim(),
                    Remote = remote.Text.Trim(), MountPoint = mountPoint.Text.Trim(), AutoMount = auto.IsOn, Options = options
                };
                CommandBuilder.ValidateMount(profile);
                if (_state.Mounts.Any(m => m.Id != profile.Id && m.MountPoint.Equals(profile.MountPoint, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("此挂载点已被另一个挂载配置使用。");
                if (existing == null) _state.Mounts.Add(profile);
                else _state.Mounts[_state.Mounts.IndexOf(existing)] = profile;
                SaveState();
            }
            catch (Exception ex) { args.Cancel = true; error.Message = CleanLog(ex.Message); error.IsOpen = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            RefreshMountRows();
            RefreshOverviewCounts();
            Notify(defaultsOnly ? "默认挂载参数已保存。" : "挂载配置已保存。");
        }
    }

    private string NextDrive()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name.TrimEnd('\\')).Concat(_state.Mounts.Select(m => m.MountPoint)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var letter = 'Z'; letter >= 'D'; letter--) if (!used.Contains(letter + ":")) return letter + ":";
        return "";
    }

    private Dictionary<string, Control> BuildMountOptionEditors(Dictionary<string, string> values, StackPanel parent)
    {
        var controls = new Dictionary<string, Control>();
        string Get(string key) => values.TryGetValue(key, out var value) ? value : "";
        void Group(string title, (string Key, string Caption)[] fields, bool expanded = false)
        {
            var panel = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < fields.Length; i++)
            {
                if (i % 2 == 0) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var (key, caption) = fields[i];
                Control input = key switch
                {
                    "vfs_cache_mode" => SelectInput("Option" + SafeName(key), caption, ["off", "minimal", "writes", "full"], Get(key)),
                    "log_level" => SelectInput("Option" + SafeName(key), caption, ["ERROR", "NOTICE", "INFO", "DEBUG"], Get(key)),
                    "network_mode" or "no_check_certificate" or "async_read" or "ignore_case" or "progress" or "links" or "read_only" => Toggle("Option" + SafeName(key), caption, Get(key).Equals("true", StringComparison.OrdinalIgnoreCase)),
                    _ => TextInput("Option" + SafeName(key), caption, Get(key))
                };
                controls[key] = input;
                Grid.SetColumn(input, i % 2);
                Grid.SetRow(input, i / 2);
                panel.Children.Add(input);
            }
            parent.Children.Add(new Expander { Header = title, Content = panel, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        Group("磁盘与缓存", [
            ("vfs_cache_mode", "VFS 缓存模式"), ("cache_dir", "缓存文件夹"),
            ("vfs_cache_max_size", "最大缓存容量（如 20G）"), ("vfs_cache_max_age", "缓存最长保留时间（如 1h）"),
            ("vfs_cache_poll_interval", "缓存检查间隔（如 1m）"), ("dir_cache_time", "目录缓存时间（如 5m）"),
            ("vfs_disk_space_total_size", "显示的磁盘总容量（如 8T）"), ("file_perms", "文件权限（如 0777）")], true);
        Group("读写与并发", [
            ("vfs_read_ahead", "预读容量（如 128M）"), ("vfs_read_chunk_size", "读取分块大小（如 128M）"),
            ("vfs_read_chunk_size_limit", "读取分块上限（如 2G）"), ("buffer_size", "单文件内存缓冲（如 16M）"),
            ("transfers", "并发上传数"), ("multi_thread_streams", "多线程数据流数"),
            ("bwlimit", "传输限速（空值为不限速）"), ("read_only", "只读挂载")]);
        Group("超时与重试", [
            ("timeout", "I/O 超时（如 5m）"), ("contimeout", "连接超时（如 1m）"),
            ("retries", "重试次数"), ("retries_sleep", "重试间隔（如 10s）")]);
        Group("Windows 与兼容选项", [
            ("network_mode", "显示为网络驱动器"), ("async_read", "异步读取"),
            ("ignore_case", "忽略文件名大小写"), ("links", "转换符号链接"),
            ("no_check_certificate", "跳过 TLS 证书验证"), ("progress", "记录进度"), ("log_level", "日志级别")]);
        parent.Children.Add(Hint("跳过 TLS 证书验证会降低连接安全性，默认关闭。缓存 full 模式需要足够本地磁盘空间；大缓冲与高并发会增加内存占用。"));
        return controls;
    }
}
