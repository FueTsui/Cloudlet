using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private readonly TextBox _transferSource = TextInput("TransferSource", "源路径", placeholder: "远程名称:目录 或本地绝对路径");
    private readonly TextBox _transferDestination = TextInput("TransferDestination", "目标路径", placeholder: "远程名称:目录 或本地绝对路径");
    private readonly ComboBox _transferOperation = SelectInput("TransferOperation", "任务类型", ["复制 · copy", "同步 · sync", "校验 · check"]);
    private readonly ToggleSwitch _transferDryRun = Toggle("TransferDryRun", "仅预演，不修改文件", true);
    private readonly CheckBox _transferChecksum = new() { Name = "TransferChecksum", Content = "使用校验和比较", IsChecked = false };
    private readonly CheckBox _transferIgnoreExisting = new() { Name = "TransferIgnoreExisting", Content = "跳过目标中已存在的文件", IsChecked = false };
    private readonly CheckBox _transferSizeOnly = new() { Name = "TransferSizeOnly", Content = "仅比较大小", IsChecked = false };
    private readonly TextBox _transferBandwidth = TextInput("TransferBandwidth", "总传输限速", "", "例如 10M，空值为不限速");
    private readonly NumberBox _transferParallel = new() { Name = "TransferParallel", Header = "并发传输数", Value = 4, Minimum = 1, Maximum = 128, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _transferCheckers = new() { Name = "TransferCheckers", Header = "并发检查数", Value = 8, Minimum = 1, Maximum = 256, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly TextBox _transferIncludes = TextInput("TransferIncludes", "包含规则（每行一条）", "", "例如 *.jpg");
    private readonly TextBox _transferExcludes = TextInput("TransferExcludes", "排除规则（每行一条）", "", "例如 /temp/**");
    private readonly StackPanel _transferForm = new() { Spacing = 14 };
    private readonly ContentControl _transferFormHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _operationDescription = Label("");
    private readonly TextBlock _previewStatus = Hint("同步实际执行前，需要完成一次相同参数的成功预演。");
    private readonly TextBlock _transferStatus = Label("没有正在运行的任务", 16, true);
    private readonly TextBlock _transferProgressText = Hint("执行后显示 rclone 返回的进度、速度和结果。");
    private readonly ProgressBar _transferProgress = new() { Minimum = 0, Maximum = 100, Value = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _transferLog = new() { Name = "TransferLog", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180, MaxHeight = 280 };
    private readonly Queue<string> _transferLogLines = new();
    private readonly StackPanel _transferHistory = new() { Spacing = 10 };
    private Button _transferCancelButton = null!;
    private CancellationTokenSource? _transferCancellation;
    private Task<CommandResult>? _transferTask;
    private TransferRequest? _activeTransfer;
    private string? _previewFingerprint;

    private FrameworkElement BuildTransfersPage()
    {
        var page = Page("传输任务", "复制、同步或校验文件；通过预演检查将要发生的变化。", out var body);
        _transferForm.Children.Add(_transferOperation);
        _transferForm.Children.Add(_operationDescription);
        _transferForm.Children.Add(PathPickerRow(_transferSource, "PickTransferSource"));
        _transferForm.Children.Add(PathPickerRow(_transferDestination, "PickTransferDestination"));
        _transferForm.Children.Add(Hint("远程格式：名称:目录。复制与同步会处理源目录的内容，不会自动再创建一层同名目录。"));
        _transferForm.Children.Add(_transferDryRun);
        _transferForm.Children.Add(_previewStatus);
        _transferIncludes.AcceptsReturn = true;
        _transferIncludes.MinHeight = 90;
        _transferExcludes.AcceptsReturn = true;
        _transferExcludes.MinHeight = 90;
        var settings = Stack(
            TwoColumns(_transferBandwidth, TwoColumns(_transferParallel, _transferCheckers)),
            _transferChecksum, _transferSizeOnly, _transferIgnoreExisting,
            TwoColumns(_transferIncludes, _transferExcludes),
            Hint("规则采用 rclone glob 语法。仅比较大小与使用校验和不能同时开启。"));
        _transferForm.Children.Add(new Expander { Header = "过滤与传输设置", Content = settings, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _transferForm.Children.Add(ActionButton("StartTransfer", "开始任务", StartTransferAsync, "\uE768", true));
        _transferFormHost.Content = _transferForm;
        body.Children.Add(Card(_transferFormHost));

        _transferCancelButton = SimpleButton("CancelTransfer", "取消任务", () =>
        {
            _transferCancellation?.Cancel();
            _transferStatus.Text = "正在取消任务…";
            _transferCancelButton.IsEnabled = false;
        }, "\uE71A");
        _transferCancelButton.IsEnabled = false;
        body.Children.Add(Card(Stack(Horizontal(_transferStatus, _transferCancelButton), _transferProgress,
            _transferProgressText, _transferLog,
            SimpleButton("CopyTransferLog", "复制任务日志", () =>
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(_transferLog.Text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                Notify("任务日志已复制。");
            }))));
        body.Children.Add(Label("本次会话记录", 18, true));
        _transferHistory.Children.Add(Hint("完成的任务会在这里显示。关闭应用后不会保留此列表。"));
        body.Children.Add(_transferHistory);

        _transferOperation.SelectionChanged += (_, _) => { InvalidatePreview(); UpdateOperationDescription(); };
        foreach (var input in new[] { _transferSource, _transferDestination, _transferBandwidth, _transferIncludes, _transferExcludes }) input.TextChanged += (_, _) => InvalidatePreview();
        foreach (var check in new[] { _transferChecksum, _transferSizeOnly, _transferIgnoreExisting })
        {
            check.Checked += (_, _) => InvalidatePreview();
            check.Unchecked += (_, _) => InvalidatePreview();
        }
        _transferParallel.ValueChanged += (_, _) => InvalidatePreview();
        _transferCheckers.ValueChanged += (_, _) => InvalidatePreview();
        _transferDryRun.Toggled += (_, _) => UpdateOperationDescription();
        UpdateOperationDescription();
        return page;
    }

    private Grid PathPickerRow(TextBox input, string name)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(input);
        var pick = ActionButton(name, "选择本地目录", async () =>
        {
            var path = await NativeIntegration.PickFolderAsync(this);
            if (path != null) input.Text = path;
        }, "\uE8B7");
        Grid.SetColumn(pick, 1);
        row.Children.Add(pick);
        return row;
    }

    private static Grid TwoColumns(FrameworkElement first, FrameworkElement second)
    {
        var grid = new Grid { ColumnSpacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(first);
        Grid.SetColumn(second, 1);
        grid.Children.Add(second);
        return grid;
    }

    private void UpdateOperationDescription()
    {
        _operationDescription.Text = _transferOperation.SelectedIndex switch
        {
            1 => "同步让目标内容与源端一致：会更新文件，并删除目标中源端不存在的文件。",
            2 => "校验比较源与目标的文件大小和可用哈希，报告差异，不修改文件。",
            _ => "复制新增和已更改的文件，保留目标中额外存在的文件。"
        };
    }

    private void InvalidatePreview()
    {
        _previewFingerprint = null;
        _previewStatus.Text = "同步实际执行前，需要完成一次相同参数的成功预演。修改参数后需重新预演。";
    }

    private TransferRequest ReadTransferRequest()
    {
        if (double.IsNaN(_transferParallel.Value) || double.IsNaN(_transferCheckers.Value) || _transferParallel.Value % 1 != 0 || _transferCheckers.Value % 1 != 0)
            throw new ArgumentException("并发数量必须是整数。");
        return new TransferRequest
        {
            Operation = _transferOperation.SelectedIndex switch { 1 => TransferOperation.Sync, 2 => TransferOperation.Check, _ => TransferOperation.Copy },
            Source = _transferSource.Text.Trim(), Destination = _transferDestination.Text.Trim(),
            DryRun = _transferDryRun.IsOn, Checksum = _transferChecksum.IsChecked == true,
            SizeOnly = _transferSizeOnly.IsChecked == true, IgnoreExisting = _transferIgnoreExisting.IsChecked == true,
            BandwidthLimit = _transferBandwidth.Text.Trim(), Transfers = (int)_transferParallel.Value, Checkers = (int)_transferCheckers.Value,
            Includes = SplitRules(_transferIncludes.Text), Excludes = SplitRules(_transferExcludes.Text)
        };
    }

    private static List<string> SplitRules(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private string TransferFingerprint(IEnumerable<string> args)
    {
        var configIdentity = "missing";
        if (File.Exists(_state.Settings.ConfigPath))
        {
            using var stream = File.OpenRead(_state.Settings.ConfigPath);
            configIdentity = Convert.ToHexString(SHA256.HashData(stream));
        }
        var scope = new[] { _state.Settings.ConfigPath, configIdentity, _state.Settings.RclonePath, File.GetLastWriteTimeUtc(_state.Settings.RclonePath).Ticks.ToString(CultureInfo.InvariantCulture) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", scope.Concat(args.Where(a => a != "--dry-run"))))));
    }
    private void EnsureTransferIdle() { if (_activeTransfer != null) throw new InvalidOperationException("已有任务正在运行，请等待完成或先取消任务。"); }
    private bool TransferUsesRemote(string name) => _activeTransfer != null && (_activeTransfer.Source.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) || _activeTransfer.Destination.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));

    private async Task StartTransferAsync()
    {
        if (_exitPending) return;
        EnsureTransferIdle();
        var request = ReadTransferRequest();
        var args = CommandBuilder.BuildTransfer(request);
        var fingerprint = TransferFingerprint(args);
        if (request.Operation == TransferOperation.Sync && !request.DryRun)
        {
            if (_previewFingerprint != fingerprint) throw new InvalidOperationException("请先开启“仅预演”并运行一次同步。预演成功后，在不改变其他参数的情况下关闭“仅预演”并执行。");
            if (!await ConfirmAsync("执行同步并允许删除目标文件？", $"源：{request.Source}\n目标：{request.Destination}\n\n目标中源端不存在的文件将被删除。请确认目标路径，并查看刚刚完成的预演日志。", "执行同步")) return;
            if (_exitPending) return;
            if (TransferFingerprint(args) != fingerprint) { InvalidatePreview(); throw new InvalidOperationException("运行环境或连接配置发生变化，请重新预演。"); }
            _previewFingerprint = null;
            _previewStatus.Text = "预演凭证已使用。下次实际同步前需要重新预演。";
        }
        if (request.Operation == TransferOperation.Sync && request.DryRun) InvalidatePreview();
        if (_exitPending) return;
        _activeTransfer = request;
        _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _transferFormHost.IsEnabled = false;
        _transferCancelButton.IsEnabled = true;
        _transferProgress.IsIndeterminate = true;
        _transferProgress.Value = 0;
        _transferLogLines.Clear();
        _transferLog.Text = "";
        var operation = request.Operation switch { TransferOperation.Sync => "同步", TransferOperation.Check => "校验", _ => "复制" };
        var label = (request.DryRun ? "预演 · " : "") + operation;
        _transferStatus.Text = label + "正在运行";
        _transferProgressText.Text = "正在启动 rclone…";
        AppendTransferLog($"{label}：{request.Source} → {request.Destination}");
        var started = DateTimeOffset.Now;
        string outcome;
        try
        {
            var progress = new Progress<string>(AppendTransferLog);
            _transferTask = _service.RunAsync(args, _transferCancellation.Token, progress);
            var result = await _transferTask;
            if (result.Cancelled)
            {
                outcome = "已取消";
                _transferStatus.Text = label + "已取消";
                _transferProgressText.Text = "任务已停止。已完成的文件操作不会自动撤销。";
                Notify("传输任务已取消。", InfoBarSeverity.Informational);
            }
            else if (result.Success)
            {
                outcome = "已完成";
                _transferStatus.Text = label + "已完成";
                _transferProgress.Value = 100;
                _transferProgressText.Text = $"rclone 成功退出 · 用时 {(DateTimeOffset.Now - started).TotalSeconds:0.0} 秒";
                if (request.Operation == TransferOperation.Sync && request.DryRun)
                {
                    _previewFingerprint = fingerprint;
                    _previewStatus.Text = $"同步预演已通过（{DateTime.Now:HH:mm:ss}）。查看日志后，可关闭“仅预演”执行；修改其他参数将使预演失效。";
                }
                Notify(request.DryRun ? "预演完成，没有修改文件。请查看日志中的操作计划。" : operation + "完成。");
            }
            else
            {
                outcome = "失败";
                _transferStatus.Text = label + "失败";
                _transferProgressText.Text = $"rclone 退出码 {result.ExitCode}，请查看日志。";
                Notify($"{operation}未完成（退出码 {result.ExitCode}）。" + result.Error, InfoBarSeverity.Error);
                if (request.Operation == TransferOperation.Sync) InvalidatePreview();
            }
            AddTransferHistory(label, outcome, request, started);
        }
        catch (Exception ex)
        {
            _transferStatus.Text = label + "未完成";
            _transferProgressText.Text = "任务未能正常完成。";
            AppendTransferLog(ex.Message);
            AddTransferHistory(label, "失败", request, started);
            if (request.Operation == TransferOperation.Sync) InvalidatePreview();
            throw;
        }
        finally
        {
            _transferProgress.IsIndeterminate = false;
            _transferCancelButton.IsEnabled = false;
            _transferFormHost.IsEnabled = true;
            _activeTransfer = null;
            _transferCancellation.Dispose();
            _transferCancellation = null;
            _transferTask = null;
        }
    }

    private void AppendTransferLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        line = CleanLog(line.Trim());
        _transferLogLines.Enqueue(line);
        while (_transferLogLines.Count > 250) _transferLogLines.Dequeue();
        _transferLog.Text = string.Join(Environment.NewLine, _transferLogLines);
        AppendLog(line);
        var percent = Regex.Match(line, @"\b(\d{1,3}(?:\.\d+)?)%");
        if (percent.Success && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            _transferProgress.IsIndeterminate = false;
            _transferProgress.Value = Math.Clamp(value, 0, 100);
            _transferProgressText.Text = line;
        }
        else if (line.Contains("/s") || line.Contains("ETA")) _transferProgressText.Text = line;
    }

    private void AddTransferHistory(string operation, string outcome, TransferRequest request, DateTimeOffset started)
    {
        if (_transferHistory.Children.Count == 1 && _transferHistory.Children[0] is TextBlock) _transferHistory.Children.Clear();
        _transferHistory.Children.Insert(0, Card(Stack(Label(operation + " · " + outcome, 14, true),
            Label(request.Source + " → " + request.Destination, 12), Hint($"{started:yyyy-MM-dd HH:mm:ss} · {(DateTimeOffset.Now - started).TotalSeconds:0.0} 秒")), new Thickness(16)));
        while (_transferHistory.Children.Count > 20) _transferHistory.Children.RemoveAt(_transferHistory.Children.Count - 1);
    }
}
