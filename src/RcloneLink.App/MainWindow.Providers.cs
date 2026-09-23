using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RcloneLink.Core;

namespace RcloneLink.App;

public sealed partial class MainWindow
{
    private readonly ContentControl _providerContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _providerStageText = Label("1  选择存储类型", 18, true);
    private readonly TextBlock _providerActivity = Hint("");
    private readonly ProgressRing _providerSpinner = new() { Width = 20, Height = 20, IsActive = false };
    private readonly InfoBar _providerError = new() { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = true };
    private readonly TextBox _providerSearch = TextInput("ProviderSearch", "搜索存储提供商", placeholder: "名称或类型，例如 OneDrive、S3、SFTP");
    private readonly ListView _providerList = new() { Name = "ProviderCatalog", IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 340, MinHeight = 180 };
    private readonly TextBlock _providerCount = Hint("");
    private TextBox _providerRemoteName = TextInput("ProviderRemoteName", "连接名称 *", placeholder: "例如：工作网盘");
    private readonly Grid _providerQuickChoices = new() { ColumnSpacing = 10, RowSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
    private Border? _providerCatalogCard;
    private readonly Dictionary<string, string> _providerFormValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProviderFieldEditor> _providerFields = new();
    private IReadOnlyList<RcloneProvider> _providerCatalog = Array.Empty<RcloneProvider>();
    private RcloneProvider? _selectedProvider;
    private ProviderConfigurationSession? _providerSession;
    private ProviderFieldEditor? _providerQuestion;
    private Button _providerNext = null!;
    private Button _providerBack = null!;
    private Button _providerCancel = null!;
    private Button _providerReopenAuthorization = null!;
    private CancellationTokenSource? _providerOperationCancellation;
    private Task? _providerPendingTask;
    private string _providerStage = "select";
    private string _providerCatalogSource = "";
    private string? _providerAuthorizationUrl;
    private bool _providerBusy;
    private bool _providerCancelling;
    private bool _providerRendering;
    private bool ProviderWizardActive => _selectedProvider != null || _providerSession != null || _providerBusy;

    private sealed class ProviderFieldEditor
    {
        public required RcloneProviderOption Option { get; init; }
        public required Control Input { get; init; }
        public required FrameworkElement View { get; init; }
        public string Read()
        {
            if (Input is PasswordBox password) return password.Password;
            if (Input is TextBox text) return text.Text.Trim();
            if (Input is ComboBox combo)
            {
                if (!combo.IsEditable) return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                var textValue = combo.Text.Trim();
                var matching = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), textValue, StringComparison.Ordinal));
                return matching?.Tag?.ToString() ?? textValue;
            }
            return "";
        }
    }

    private FrameworkElement BuildProvidersPage()
    {
        var page = Page("添加存储连接", "在这里完成连接设置；需要登录时，在浏览器中授权后自动继续。", out var body);
        _providerNext = ActionButton("ProviderNext", "继续", ContinueProviderWizardAsync, "\uE72A", true);
        _providerBack = ActionButton("ProviderBack", "上一步", PreviousProviderStepAsync, "\uE72B");
        _providerCancel = ActionButton("ProviderCancel", "取消并返回", async () =>
        {
            if (ProviderWizardActive && !await ConfirmAsync("取消连接设置？", "未保存的设置将被丢弃。现有连接不会受到影响。", "取消设置")) return;
            await CancelProviderWizardAsync(true);
        });
        _providerReopenAuthorization = SimpleButton("ProviderReopenAuthorization", "重新打开授权页面", () =>
        {
            if (_providerAuthorizationUrl != null) NativeIntegration.OpenAuthorizationUrl(_providerAuthorizationUrl);
        }, "\uE774");
        _providerReopenAuthorization.Visibility = Visibility.Collapsed;
        body.Children.Add(_providerStageText);
        body.Children.Add(_providerContent);
        body.Children.Add(_providerError);
        body.Children.Add(Horizontal(_providerSpinner, _providerActivity));
        body.Children.Add(_providerReopenAuthorization);
        body.Children.Add(Horizontal(_providerNext, _providerBack, _providerCancel));
        _providerSearch.TextChanged += (_, _) => FilterProviderCatalog();
        _providerList.ItemClick += (_, args) =>
        {
            if ((args.ClickedItem as ListViewItem)?.Tag is RcloneProvider provider) SelectProvider(provider);
        };
        ShowProviderCatalog();
        return page;
    }

    private async Task OpenProviderSetupAsync()
    {
        Navigate("providers");
        InvalidatePreview();
        if (ProviderWizardActive) return;
        if (_providerCatalog.Count == 0 || _providerCatalogSource != _state.Settings.RclonePath) await LoadProviderCatalogAsync();
        else ShowProviderCatalog();
    }

    private async Task LoadProviderCatalogAsync()
    {
        await ExecuteProviderOperationAsync(async token =>
        {
            _providerCatalog = await _service.GetProvidersAsync(token);
            _providerCatalogSource = _state.Settings.RclonePath;
            ShowProviderCatalog();
        }, "正在读取可用的存储提供商…");
    }

    private void ShowProviderCatalog()
    {
        _providerStage = "select";
        _providerStageText.Text = "1  选择存储类型";
        if (_providerCatalogCard == null)
        {
            for (var column = 0; column < 3; column++) _providerQuickChoices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var panel = Stack(Label("常用存储", 16, true), _providerQuickChoices, _providerSearch, _providerList,
                Horizontal(_providerCount, ActionButton("RefreshProviderCatalog", "刷新提供商列表", LoadProviderCatalogAsync, "\uE72C")));
            _providerCatalogCard = Card(panel);
        }
        // Keep catalog controls in their original native parents. Parent getters
        // are not reliable ownership indicators when a page is detached.
        _providerQuickChoices.Children.Clear();
        _providerQuickChoices.RowDefinitions.Clear();
        var preferred = new[] { "onedrive", "drive", "s3", "sftp", "ftp", "smb" };
        var position = 0;
        foreach (var key in preferred)
        {
            var provider = _providerCatalog.FirstOrDefault(p => p.Name == key);
            if (provider == null) continue;
            if (position % 3 == 0) _providerQuickChoices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = SimpleButton("ProviderQuick" + SafeName(key), ProviderTitle(provider), () => SelectProvider(provider));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(button, position % 3);
            Grid.SetRow(button, position / 3);
            _providerQuickChoices.Children.Add(button);
            position++;
        }
        _providerContent.Content = _providerCatalogCard;
        FilterProviderCatalog();
        _providerNext.Visibility = Visibility.Collapsed;
        _providerBack.Visibility = Visibility.Collapsed;
        _providerCancel.Content = "返回远程连接";
    }

    private void FilterProviderCatalog()
    {
        var query = _providerSearch.Text.Trim();
        _providerList.Items.Clear();
        var preferred = new[] { "onedrive", "drive", "s3", "sftp", "ftp", "smb", "webdav", "dropbox", "box", "azureblob" };
        var matches = _providerCatalog.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || ProviderTitle(p).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(p => { var index = Array.IndexOf(preferred, p.Name); return index < 0 ? 99 : index; }).ThenBy(p => p.Description, StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach (var provider in matches)
        {
            var description = FirstHelpParagraph(provider.Description, 180);
            var view = Stack(Label(ProviderTitle(provider) + (provider.Hide ? "（兼容类型）" : ""), 15, true), Hint(description));
            view.Spacing = 3;
            var item = new ListViewItem { Tag = provider, Content = view, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10) };
            AutomationProperties.SetName(item, ProviderTitle(provider));
            _providerList.Items.Add(item);
        }
        _providerCount.Text = _providerCatalog.Count == 0 ? "点击“刷新提供商列表”读取当前 rclone 支持的类型。" : matches.Length == 0 ? "没有匹配的提供商，请尝试英文名称或类型。" : $"显示 {matches.Length} / {_providerCatalog.Count} 个提供商 · 选择一项继续";
    }

    private static string ProviderTitle(RcloneProvider provider) => provider.Name switch
    {
        "onedrive" => "Microsoft OneDrive", "drive" => "Google Drive", "s3" => "S3 兼容对象存储", "sftp" => "SFTP / SSH", "ftp" => "FTP / FTPS", "smb" => "SMB / Windows 共享", "webdav" => "WebDAV", "azureblob" => "Azure Blob Storage", _ => provider.Description.Length is > 0 and < 70 ? provider.Description : provider.Name
    };

    private void SelectProvider(RcloneProvider provider)
    {
        if (_providerBusy || _providerSession != null) return;
        if (_selectedProvider?.Name != provider.Name)
        {
            ClearProviderFields();
            _providerFormValues.Clear();
            _providerRemoteName.Text = "";
        }
        _selectedProvider = provider;
        _providerError.IsOpen = false;
        RenderProviderForm();
    }

    private void RenderProviderForm()
    {
        if (_selectedProvider == null) return;
        // Form instances own their controls; retain only values when rebuilding.
        _providerRemoteName = TextInput("ProviderRemoteName", "连接名称 *", _providerRemoteName.Text, "例如：工作网盘");
        _providerRendering = true;
        _providerStage = "form";
        _providerStageText.Text = "2  设置 " + ProviderTitle(_selectedProvider);
        _providerFields.Clear();
        var panel = Stack(_providerRemoteName, Hint("标有 * 的项目为必填。其余项目可以保留默认值。"));
        var basic = new StackPanel { Spacing = 18 };
        var advanced = new StackPanel { Spacing = 18 };
        var branch = _providerFormValues.GetValueOrDefault("provider", _selectedProvider.Options.FirstOrDefault(o => o.Name == "provider")?.DefaultText ?? "");
        var options = _selectedProvider.Options.Where(o => (o.Hide & 2) == 0 && ProviderConditionMatches(o.Provider, branch)).GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToArray();
        foreach (var option in options)
        {
            var value = _providerFormValues.GetValueOrDefault(option.Name, option.DefaultText);
            var field = CreateProviderEditor(option, value, branch, "ProviderField");
            _providerFields.Add(field);
            (option.Advanced ? advanced : basic).Children.Add(field.View);
            if (option.Name == "provider")
            {
                if (field.Input is ComboBox combo)
                {
                    combo.SelectionChanged += (_, args) =>
                    {
                        if (args.AddedItems.FirstOrDefault() is ComboBoxItem selected) RefreshProviderBranch(field, selected.Tag?.ToString() ?? "");
                    };
                    combo.TextSubmitted += (_, _) => RefreshProviderBranch(field);
                    combo.LostFocus += (_, _) => RefreshProviderBranch(field);
                }
                else if (field.Input is TextBox text) text.LostFocus += (_, _) => RefreshProviderBranch(field);
            }
        }
        if (basic.Children.Count > 0) panel.Children.Add(basic);
        if (advanced.Children.Count > 0) panel.Children.Add(new Expander { Header = $"高级设置（{advanced.Children.Count} 项）", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(Hint("连接仅在向导完成并保存后加入列表。取消设置不会更改现有连接。"));
        _providerContent.Content = Card(panel);
        _providerNext.Visibility = Visibility.Visible;
        _providerNext.Content = "继续";
        _providerBack.Visibility = Visibility.Visible;
        _providerCancel.Content = "取消并返回";
        _providerActivity.Text = "";
        _providerRendering = false;
    }

    private void RefreshProviderBranch(ProviderFieldEditor field, string? selectedValue = null)
    {
        if (_providerRendering || _providerBusy || _selectedProvider == null) return;
        var previous = _providerFormValues.GetValueOrDefault("provider", _selectedProvider.Options.FirstOrDefault(o => o.Name == "provider")?.DefaultText ?? "");
        var next = selectedValue ?? field.Read();
        if (previous == next) return;
        CaptureProviderFormValues();
        foreach (var key in _selectedProvider.Options.Where(o => !string.IsNullOrWhiteSpace(o.Provider)).Select(o => o.Name).Distinct()) _providerFormValues.Remove(key);
        _providerFormValues["provider"] = next;
        RenderProviderForm();
    }

    private ProviderFieldEditor CreateProviderEditor(RcloneProviderOption option, string value, string branch, string namePrefix)
    {
        var title = ProviderFieldTitle(option.Name) + (option.Required ? " *" : "");
        var name = namePrefix + SafeName(option.Name);
        Control input;
        if (IsProviderSecret(option))
        {
            input = new PasswordBox { Name = name, Header = title, Password = value, PasswordRevealMode = PasswordRevealMode.Peek, HorizontalAlignment = HorizontalAlignment.Stretch };
        }
        else if (option.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            var combo = new ComboBox { Name = name, Header = title, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.Items.Add(new ComboBoxItem { Content = "是", Tag = "true" });
            combo.Items.Add(new ComboBoxItem { Content = "否", Tag = "false" });
            combo.SelectedIndex = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            input = combo;
        }
        else if (option.Examples.Count > 0)
        {
            var combo = new ComboBox { Name = name, Header = title, IsEditable = !option.Exclusive, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = option.Exclusive ? "请选择" : "选择示例或输入自定义值", MaxDropDownHeight = 380 };
            if (!option.Required) combo.Items.Add(new ComboBoxItem { Content = "使用默认值", Tag = "" });
            foreach (var example in option.Examples.Where(e => ProviderConditionMatches(e.Provider, branch)))
            {
                var shortHelp = FirstHelpParagraph(example.Help, 90);
                var item = new ComboBoxItem { Content = string.IsNullOrWhiteSpace(shortHelp) ? example.Value : example.Value + " — " + shortHelp, Tag = example.Value };
                ToolTipService.SetToolTip(item, example.Help);
                combo.Items.Add(item);
                if (example.Value == value) combo.SelectedItem = item;
            }
            if (combo.SelectedItem == null)
            {
                if (!option.Required && value.Length == 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
                else if (combo.IsEditable) combo.Text = value;
            }
            if (combo.IsEditable)
            {
                // WinUI raises SelectionChanged before it synchronizes editable
                // Text. Keep the display text and canonical example value aligned.
                combo.Text = (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? value;
                combo.SelectionChanged += (_, args) =>
                {
                    if (args.AddedItems.FirstOrDefault() is ComboBoxItem selected) combo.Text = selected.Content?.ToString() ?? "";
                };
                combo.TextSubmitted += (_, args) =>
                {
                    var submitted = args.Text.Trim();
                    var match = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), submitted, StringComparison.Ordinal) || string.Equals(item.Content?.ToString(), submitted, StringComparison.Ordinal));
                    combo.SelectedItem = match;
                    combo.Text = match?.Content?.ToString() ?? submitted;
                    args.Handled = true;
                };
            }
            input = combo;
        }
        else input = TextInput(name, title, value, option.DefaultText.Length > 0 ? "默认：" + option.DefaultText : "");
        AutomationProperties.SetName(input, title);
        var help = option.Name == "config_token" ? "可返回连接设置重新发起浏览器授权，或输入由服务商提供的授权令牌。" : option.Help;
        var panel = Stack(input);
        panel.Spacing = 6;
        if (!string.IsNullOrWhiteSpace(help))
        {
            panel.Children.Add(Hint(FirstHelpParagraph(help, 230)));
            if (help.Length > 230 || help.Contains("\n\n")) panel.Children.Add(new Expander { Header = "完整参数说明", Content = Label(help, 12), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        return new ProviderFieldEditor { Option = option, Input = input, View = panel };
    }

    private static bool IsProviderSecret(RcloneProviderOption option)
    {
        if (option.Name is "user" or "username" or "host" or "url" or "endpoint" or "client_id" or "access_key_id" or "application_key_id" or "account" or "account_id" or "tenant" or "tenant_id" or "domain" or "drive_id" or "root_folder_id" or "team_drive" or "bucket" or "container" or "key_file" or "known_hosts_file" or "service_account_file") return false;
        return option.IsPassword || option.Sensitive || option.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || option.Name.Contains("token", StringComparison.OrdinalIgnoreCase) || option.Name is "password" or "pass" or "key_pem" or "private_key";
    }

    private static bool ProviderConditionMatches(string condition, string selected)
    {
        if (string.IsNullOrWhiteSpace(condition) || string.IsNullOrEmpty(selected)) return true;
        var negate = condition.StartsWith('!');
        var names = (negate ? condition[1..] : condition).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var match = names.Contains(selected, StringComparer.Ordinal);
        return negate ? !match : match;
    }

    private void CaptureProviderFormValues()
    {
        foreach (var field in _providerFields) _providerFormValues[field.Option.Name] = field.Read();
    }

    private static void ValidateProviderField(ProviderFieldEditor field)
    {
        var value = field.Read();
        if (field.Option.Required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请填写“" + ProviderFieldTitle(field.Option.Name) + "”。");
        if (value.Contains('\0')) throw new ArgumentException("字段不能包含空字符。");
        if (value.Length > 0 && (field.Option.Type is "int" or "int64" or "uint" or "uint64") && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new ArgumentException("“" + ProviderFieldTitle(field.Option.Name) + "”需要填写整数。");
    }

    private async Task ContinueProviderWizardAsync()
    {
        if (_providerBusy || _selectedProvider == null || _exitPending) return;
        _providerError.IsOpen = false;
        if (_providerStage == "form")
        {
            if (string.IsNullOrWhiteSpace(_providerRemoteName.Text)) throw new ArgumentException("请填写连接名称。");
            foreach (var field in _providerFields) ValidateProviderField(field);
            CaptureProviderFormValues();
            var parameters = _providerFields.ToDictionary(f => f.Option.Name, f => f.Read(), StringComparer.OrdinalIgnoreCase);
            await ExecuteProviderOperationAsync(async token =>
            {
                _providerSession = await _service.BeginProviderConfigurationAsync(_providerRemoteName.Text.Trim(), _selectedProvider.Name, parameters, token);
                _providerSession.AuthorizationUrlAvailable += OnProviderAuthorizationAvailable;
                token.ThrowIfCancellationRequested();
                RenderProviderQuestion();
            }, "正在准备连接设置…");
            return;
        }
        if (_providerSession == null) return;
        if (_providerSession.CurrentStep.Complete)
        {
            var remoteName = _providerSession.Name;
            await ExecuteProviderOperationAsync(async token =>
            {
                await _providerSession.CommitAsync(token);
                await DisposeProviderSessionAsync();
                ClearProviderDraft();
                await RefreshRemotesAsync();
                Navigate("remotes");
                Notify($"已添加连接 {remoteName}。可以测试连接、浏览文件或添加磁盘挂载。");
            }, "正在保存连接…");
            return;
        }
        var oauth = IsBrowserAuthorizationStep(_providerSession.CurrentStep.Option);
        if (!oauth && _providerQuestion != null) ValidateProviderField(_providerQuestion);
        var answer = oauth ? "true" : _providerQuestion?.Read() ?? "";
        await ExecuteProviderOperationAsync(async token =>
        {
            await _providerSession.AdvanceAsync(answer, token);
            token.ThrowIfCancellationRequested();
            _providerAuthorizationUrl = null;
            _providerReopenAuthorization.Visibility = Visibility.Collapsed;
            RenderProviderQuestion();
        }, oauth ? "正在等待浏览器授权。请在浏览器完成登录；你也可以取消此次设置。" : "正在确认设置…");
    }

    private void RenderProviderQuestion()
    {
        if (_providerSession == null) return;
        var step = _providerSession.CurrentStep;
        _providerStage = "question";
        _providerQuestion = null;
        _providerStageText.Text = "3  授权与确认";
        var panel = Stack(Label(ProviderTitle(_selectedProvider!) + " · " + _providerSession.Name, 18, true));
        if (!string.IsNullOrWhiteSpace(step.Error)) { _providerError.Message = CleanLog(step.Error); _providerError.IsOpen = true; }
        if (step.Complete)
        {
            panel.Children.Add(Label("连接设置已完成", 20, true));
            panel.Children.Add(Label("保存后，此连接将加入远程列表。你可以继续测试连接、浏览文件或配置挂载。"));
            _providerNext.Content = "保存连接";
        }
        else if (IsBrowserAuthorizationStep(step.Option))
        {
            panel.Children.Add(Label("登录并授权访问", 20, true));
            panel.Children.Add(Label("点击下方按钮，在默认浏览器中完成账户登录和访问授权。授权完成后，应用会自动继续设置。"));
            panel.Children.Add(Hint("授权期间请保持应用开启。取消设置不会向远程列表添加连接。"));
            _providerNext.Content = "在浏览器中授权";
        }
        else if (step.Option != null)
        {
            _providerQuestion = CreateProviderEditor(step.Option, step.Option.DefaultText, _providerFormValues.GetValueOrDefault("provider", ""), "ProviderQuestion");
            panel.Children.Add(_providerQuestion.View);
            _providerNext.Content = string.IsNullOrWhiteSpace(step.Error) ? "继续" : "重试";
        }
        else throw new InvalidOperationException("提供商没有返回可处理的设置步骤。请返回连接设置后重试。");
        panel.Children.Add(Hint("上一步会返回连接设置并重新开始验证；已经填写的初始表单将保留。"));
        _providerContent.Content = Card(panel);
        _providerNext.Visibility = Visibility.Visible;
        _providerBack.Visibility = Visibility.Visible;
        _providerActivity.Text = "";
    }

    private static bool IsBrowserAuthorizationStep(RcloneProviderOption? option) => option?.Name == "config_is_local";

    private void OnProviderAuthorizationAvailable(object? sender, string url) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_providerCancelling || _providerSession == null || !_providerBusy) return;
        _providerAuthorizationUrl = url;
        _providerReopenAuthorization.Visibility = Visibility.Visible;
        _providerActivity.Text = "正在等待浏览器授权。如果浏览器未打开，可点击“重新打开授权页面”。";
    });

    private async Task ExecuteProviderOperationAsync(Func<CancellationToken, Task> operation, string activity)
    {
        if (_providerBusy || _exitPending) return;
        _providerBusy = true;
        _providerCancelling = false;
        _providerError.IsOpen = false;
        _providerSpinner.IsActive = true;
        _providerActivity.Text = activity;
        _providerContent.IsEnabled = false;
        _providerNext.IsEnabled = false;
        _providerBack.IsEnabled = false;
        _providerOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            _providerPendingTask = operation(_providerOperationCancellation.Token);
            await _providerPendingTask;
        }
        catch (OperationCanceledException)
        {
            if (!_providerCancelling) { _providerError.Message = "操作已取消。可以返回连接设置后重试。"; _providerError.IsOpen = true; }
        }
        catch (Exception ex)
        {
            if (!_providerCancelling) { _providerError.Message = CleanLog(ex.Message); _providerError.IsOpen = true; _providerNext.Content = "重试"; }
        }
        finally
        {
            _providerPendingTask = null;
            _providerOperationCancellation.Dispose();
            _providerOperationCancellation = null;
            _providerBusy = false;
            _providerSpinner.IsActive = false;
            _providerActivity.Text = "";
            _providerContent.IsEnabled = true;
            _providerNext.IsEnabled = true;
            _providerBack.IsEnabled = true;
        }
    }

    private async Task PreviousProviderStepAsync()
    {
        if (_providerBusy) return;
        _providerError.IsOpen = false;
        if (_providerSession != null)
        {
            await DisposeProviderSessionAsync();
            RenderProviderForm();
        }
        else
        {
            CaptureProviderFormValues();
            _selectedProvider = null;
            ShowProviderCatalog();
        }
    }

    private async Task CancelProviderWizardAsync(bool navigateBack)
    {
        _providerCancelling = true;
        _providerOperationCancellation?.Cancel();
        var pending = _providerPendingTask;
        if (pending != null) { try { await pending; } catch { } }
        await DisposeProviderSessionAsync();
        ClearProviderDraft();
        ShowProviderCatalog();
        if (navigateBack) Navigate("remotes");
    }

    private async Task DisposeProviderSessionAsync()
    {
        var session = _providerSession;
        _providerSession = null;
        if (session != null)
        {
            session.AuthorizationUrlAvailable -= OnProviderAuthorizationAvailable;
            await session.DisposeAsync();
        }
        _providerAuthorizationUrl = null;
        _providerReopenAuthorization.Visibility = Visibility.Collapsed;
    }

    private void ClearProviderFields()
    {
        foreach (var field in _providerFields) if (field.Input is PasswordBox password) password.Password = "";
        if (_providerQuestion?.Input is PasswordBox questionPassword) questionPassword.Password = "";
        _providerFields.Clear();
        _providerQuestion = null;
    }

    private void ClearProviderDraft()
    {
        ClearProviderFields();
        _providerFormValues.Clear();
        _providerRemoteName.Text = "";
        _selectedProvider = null;
        _providerStage = "select";
        _providerError.IsOpen = false;
    }

    private static string FirstHelpParagraph(string text, int limit)
    {
        var first = (text ?? "").Split(["\r\n\r\n", "\n\n"], StringSplitOptions.None)[0].Replace('\n', ' ').Replace('\r', ' ').Trim();
        return first.Length > limit ? first[..limit] + "…" : first;
    }

    private static string ProviderFieldTitle(string name) => name switch
    {
        "provider" => "存储服务商", "host" => "服务器地址", "port" => "端口", "user" or "username" => "用户名", "pass" or "password" => "密码",
        "url" => "服务地址", "vendor" => "WebDAV 服务类型", "domain" => "域 / 工作组", "client_id" => "应用客户端 ID（可选）", "client_secret" => "应用客户端密钥（可选）",
        "region" => "服务区域", "endpoint" => "服务端点", "access_key_id" => "访问密钥 ID", "secret_access_key" => "访问密钥", "session_token" => "会话令牌",
        "env_auth" => "使用环境或实例身份凭据", "scope" => "授权范围", "token" or "config_token" => "授权令牌", "auth_url" => "自定义授权地址", "token_url" => "自定义令牌地址",
        "service_account_file" => "服务账户凭据文件", "service_account_credentials" => "服务账户凭据内容", "root_folder_id" => "根文件夹 ID", "team_drive" => "共享云端硬盘 ID",
        "drive_id" => "云端硬盘 ID", "drive_type" => "云端硬盘类型", "tenant" => "租户 ID", "config_type" => "连接类型", "config_driveid" => "选择云端硬盘", "config_team_drive" => "选择共享云端硬盘",
        "key_file" => "SSH 私钥文件", "key_pem" => "SSH 私钥内容", "key_file_pass" => "私钥密码", "pubkey" => "SSH 公钥", "key_use_agent" => "使用 SSH 代理", "known_hosts_file" => "已知主机文件",
        "tls" => "使用隐式 TLS", "explicit_tls" => "使用显式 TLS", "no_check_certificate" => "跳过 TLS 证书检查", "disable_epsv" => "禁用扩展被动模式",
        "remote" => "远程或本地路径", "bucket" => "存储桶", "account" => "账户名", "key" => "访问密钥", "sas_url" => "共享访问签名地址", "encoding" => "文件名编码",
        "description" => "连接说明", "acl" => "访问权限", "storage_class" => "存储类别", "location_constraint" => "存储桶区域", "server_side_encryption" => "服务端加密方式", "chunk_size" => "分块大小",
        "concurrency" => "并发连接数", "timeout" => "超时时间", "idle_timeout" => "空闲连接超时", "use_kerberos" => "使用 Kerberos 身份验证", "spn" => "服务主体名称",
        "config_is_local" => "在浏览器中授权", "config_refresh_token" => "刷新授权", _ => name
    };
}
