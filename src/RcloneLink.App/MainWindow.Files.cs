using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private readonly TextBox _browserPath = TextInput("BrowserPath", "当前位置", placeholder: "远程名称:目录 或 C:\\本地目录");
    private readonly ComboBox _browserRemote = SelectInput("BrowserRemote", "远程连接", []);
    private readonly ListView _fileList = new() { Name = "FileList", SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, MinHeight = 210, MaxHeight = 430 };
    private readonly TextBlock _fileSummary = Hint("选择一个连接或输入路径，然后读取目录。");
    private readonly TextBlock _sizeResult = Label("尚未统计");
    private string _loadedPath = "";
    private int _listingGeneration;

    private FrameworkElement BuildFilesPage()
    {
        var page = Page("文件浏览", "查看远程或本地目录，统计容量，并把选中的路径用于传输。", out var body);
        var locationGrid = new Grid { ColumnSpacing = 10 };
        locationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        locationGrid.Children.Add(_browserPath);
        var go = ActionButton("LoadFiles", "读取目录", LoadFilesAsync, "\uE72C", true);
        Grid.SetColumn(go, 1);
        locationGrid.Children.Add(go);
        _browserRemote.MinWidth = 210;
        _browserRemote.SelectionChanged += (_, _) =>
        {
            if (_browserRemote.SelectedItem is string remote) _browserPath.Text = remote + ":";
        };
        _browserPath.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            await RunUiAsync(LoadFilesAsync, "读取文件");
        };
        body.Children.Add(Card(Stack(locationGrid, Horizontal(_browserRemote,
            ActionButton("PickBrowserFolder", "本地文件夹", async () =>
            {
                var folder = await NativeIntegration.PickFolderAsync(this);
                if (folder == null) return;
                _browserPath.Text = folder;
                await LoadFilesAsync();
            }, "\uE8B7"),
            ActionButton("BrowserParent", "上一级", GoParentAsync, "\uE74A")))));

        var fileHeader = FileRow("名称", "大小", "修改时间", true);
        _fileList.ItemClick += async (_, args) =>
        {
            var file = (args.ClickedItem as ListViewItem)?.Tag as RcloneFile;
            if (file == null || !file.IsDir) return;
            _browserPath.Text = JoinBrowsePath(_loadedPath, file.Name);
            await RunUiAsync(LoadFilesAsync, "读取目录");
        };
        body.Children.Add(Card(Stack(fileHeader, _fileList, _fileSummary), new Thickness(16)));
        body.Children.Add(Card(Stack(
            Horizontal(ActionButton("GetDirectorySize", "统计目录容量", async () =>
            {
                var path = ReadBrowsePath();
                _sizeResult.Text = "正在统计…";
                try
                {
                    var result = await _service.GetSizeAsync(path, _lifetime.Token);
                    _sizeResult.Text = $"{result.Count:N0} 个文件 · {FormatBytes(result.Bytes)}";
                }
                catch { _sizeResult.Text = "统计未完成"; throw; }
            }), _sizeResult),
            Horizontal(SimpleButton("BrowserUseSource", "用作传输源", () =>
            {
                EnsureTransferIdle();
                _transferSource.Text = SelectedBrowsePath();
                Navigate("transfers");
            }), SimpleButton("BrowserUseDestination", "用作传输目标", () =>
            {
                EnsureTransferIdle();
                _transferDestination.Text = SelectedBrowsePath();
                Navigate("transfers");
            })), Hint("单击文件夹进入；选择文件可填入传输路径。目录传输操作处理该目录的内容。"))));
        return page;
    }

    private static Grid FileRow(string name, string size, string modified, bool header = false)
    {
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(4, 8, 4, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var title = Label(name, 13, header);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        ToolTipService.SetToolTip(title, name);
        var sizeText = Label(size, 12, header);
        var modifiedText = Label(modified, 12, header);
        grid.Children.Add(title);
        Grid.SetColumn(sizeText, 1);
        grid.Children.Add(sizeText);
        Grid.SetColumn(modifiedText, 2);
        grid.Children.Add(modifiedText);
        return grid;
    }

    private void RefreshBrowserRemotes()
    {
        var selected = _browserRemote.SelectedItem as string;
        _browserRemote.Items.Clear();
        foreach (var remote in _remotes) _browserRemote.Items.Add(remote);
        if (selected != null && _browserRemote.Items.Contains(selected)) _browserRemote.SelectedItem = selected;
        else if (string.IsNullOrWhiteSpace(_browserPath.Text) && _remotes.Count > 0) _browserRemote.SelectedIndex = 0;
    }

    private string ReadBrowsePath()
    {
        var path = _browserPath.Text.Trim();
        if (path.Length == 0) throw new ArgumentException("请输入远程目录或本地绝对路径。");
        return path.Contains(':') || Path.IsPathRooted(path) ? path : CommandBuilder.NormalizeRemote(path);
    }

    private string SelectedBrowsePath()
    {
        if (_fileList.SelectedItem is ListViewItem { Tag: RcloneFile file } && !string.IsNullOrWhiteSpace(_loadedPath)) return JoinBrowsePath(_loadedPath, file.Name);
        return ReadBrowsePath();
    }

    private static string JoinBrowsePath(string parent, string name) => Path.IsPathRooted(parent) ? Path.Combine(parent, name) : parent.TrimEnd('/') + (parent.EndsWith(':') ? "" : "/") + name;

    private async Task LoadFilesAsync()
    {
        var path = ReadBrowsePath();
        var generation = ++_listingGeneration;
        _fileSummary.Text = "正在读取目录…";
        _fileList.IsEnabled = false;
        try
        {
            var files = await _service.ListFilesAsync(path, _lifetime.Token);
            if (generation != _listingGeneration) return;
            _loadedPath = path;
            _browserPath.Text = path;
            _fileList.Items.Clear();
            foreach (var file in files.OrderByDescending(f => f.IsDir).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _fileList.Items.Add(new ListViewItem
                {
                    Tag = file, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = FileRow((file.IsDir ? "▸  " : "    ") + file.Name, file.IsDir ? "文件夹" : FormatBytes(file.Size), file.ModTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—")
                });
            }
            _fileSummary.Text = files.Count == 0 ? "此目录为空。" : $"{files.Count(f => f.IsDir):N0} 个文件夹 · {files.Count(f => !f.IsDir):N0} 个文件";
            _sizeResult.Text = "尚未统计";
        }
        catch { if (generation == _listingGeneration) _fileSummary.Text = "读取失败，请检查路径与连接。"; throw; }
        finally { if (generation == _listingGeneration) _fileList.IsEnabled = true; }
    }

    private async Task GoParentAsync()
    {
        var path = ReadBrowsePath();
        if (Path.IsPathRooted(path))
        {
            var parent = Directory.GetParent(Path.GetFullPath(path));
            if (parent == null) { Notify("已到达本地磁盘根目录。", InfoBarSeverity.Informational); return; }
            _browserPath.Text = parent.FullName;
        }
        else
        {
            var colon = path.IndexOf(':');
            if (colon < 0) throw new ArgumentException("远程路径应为 名称:目录。");
            var root = path[..(colon + 1)];
            var relative = path[(colon + 1)..].Trim('/');
            if (relative.Length == 0) { Notify("已到达远程根目录。", InfoBarSeverity.Informational); return; }
            var slash = relative.LastIndexOf('/');
            _browserPath.Text = root + (slash < 0 ? "" : relative[..slash]);
        }
        await LoadFilesAsync();
    }
}
