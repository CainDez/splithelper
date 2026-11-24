using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SplitHelper.Services;

namespace SplitHelper;

public sealed class DesktopViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppPaths _paths;
    private readonly ClientEnvironment _environment;
    private readonly SettingsStore _store;
    private readonly ProfileService _profiles;
    private readonly HelperManager _helper;
    private readonly StartupService _startup;
    private readonly ConnectionService _connections;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _backgroundOperation;
    private Task _backgroundRefresh = Task.CompletedTask;
    private CancellationTokenSource? _autoConnect;
    private string _page = "overview", _provider = "Windscribe";
    private string _savedInterface = "";
    private bool _busy, _active, _managed;
    private int _wizardStep, _settingsTab;
    private CustomRule? _selectedCustomRule, _editingCustomRule;
    private string _ruleDomain = "", _ruleTarget = "VPN";
    private bool _ruleSubdomains = true, _ruleEnabled = true, _isEditingCustomRule;
    private string _headline = "正在检查本机环境", _subtitle = "直连组件由分流助手统一管理。", _tone = "muted", _notice = "";
    public DesktopViewModel(AppPaths paths, bool safeMode)
    {
        _paths = paths; IsSafeMode = safeMode;
        _environment = new ClientEnvironment(); _store = new SettingsStore(paths); _profiles = new ProfileService(paths, _environment);
        _helper = new HelperManager(paths); _startup = new StartupService(paths);
        _connections = new ConnectionService(paths, _environment, _store, _profiles, _helper, new WindowsProxy(paths), _startup, new MihomoClient(paths.MainPipeName));
        AppSettings saved;
        try { saved = _store.Load(); }
        catch (Exception error) when (error is JsonException or IOException or System.Security.Cryptography.CryptographicException or FormatException) { saved = new(); _notice = "本机设置读取失败；原文件已保留，请重新填写后保存。"; }
        if (File.Exists(paths.MainConfig))
        {
            try
            {
                var prepared = ProfileService.ReadStandaloneSettings(paths.MainConfig);
                saved.MixedPort = prepared.MixedPort; saved.WindscribeInterface = prepared.WindscribeInterface;
                if (_connections.HasProxySession) saved.Provider = prepared.Provider;
            }
            catch (Exception error) when (error is UserError or IOException or InvalidOperationException or YamlDotNet.Core.YamlException)
            { _notice = "独立配置读取失败；文件和代理记录已保留，请先停止并还原后重新启用。"; }
        }
        _provider = saved.Provider; _savedInterface = saved.WindscribeInterface;
        var occupied = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        MixedPort = (File.Exists(paths.MainConfig) ? saved.MixedPort : Enumerable.Range(17897, 20).FirstOrDefault(port => !occupied.Contains(port))).ToString();
        AutoEnable = saved.AutoEnable; CloseToTray = saved.CloseToTray; WindscribeConfirmed = saved.WindscribeConfirmed; AllowLan = saved.AllowLan;
        try { foreach (var rule in _profiles.LoadCustomRules()) CustomRules.Add(rule); }
        catch (Exception error) when (error is UserError or IOException or JsonException)
        { _notice = "自定义规则读取失败，原文件已保留：" + error.Message; }
        CustomRules.CollectionChanged += (_, _) => Raise(nameof(CustomRuleSummary));
        AddCustomRuleCommand = Command(_ =>
        {
            BeginCustomRuleEdit(null);
            return Task.CompletedTask;
        }, available: () => !IsEditingCustomRule);
        EditCustomRuleCommand = Command(_ => { if (SelectedCustomRule is { } rule) BeginCustomRuleEdit(rule); return Task.CompletedTask; }, available: () => SelectedCustomRule is not null && !IsEditingCustomRule);
        CancelCustomRuleEditCommand = Command(_ =>
        {
            CancelCustomRuleEdit();
            Notice = "已取消编辑。";
            return Task.CompletedTask;
        }, available: () => IsEditingCustomRule);
        RemoveCustomRuleCommand = Command(async token =>
        {
            if (SelectedCustomRule is not { } rule) return;
            var index = CustomRules.IndexOf(rule); var draft = CustomRules.ToList(); draft.RemoveAt(index);
            await CommitCustomRulesAsync(draft, token);
            CustomRules.RemoveAt(index);
            SelectedCustomRule = null;
        }, available: () => SelectedCustomRule is not null && !IsEditingCustomRule);
        ToggleCustomRuleCommand = Command(async token =>
        {
            if (SelectedCustomRule is not { } rule) return;
            var index = CustomRules.IndexOf(rule); var draft = CustomRules.ToList(); draft[index] = rule with { Enabled = !rule.Enabled };
            var saved = await CommitCustomRulesAsync(draft, token);
            CustomRules[index] = saved[index];
            SelectedCustomRule = saved[index];
        }, available: () => SelectedCustomRule is not null && !IsEditingCustomRule);
        MoveCustomRuleUpCommand = Command(token => MoveCustomRuleAsync(-1, token), available: () => SelectedCustomRule is { } rule && CustomRules.IndexOf(rule) > 0 && !IsEditingCustomRule);
        MoveCustomRuleDownCommand = Command(token => MoveCustomRuleAsync(1, token), available: () => SelectedCustomRule is { } rule && CustomRules.IndexOf(rule) < CustomRules.Count - 1 && !IsEditingCustomRule);
        SaveCustomRulesCommand = Command(async token =>
        {
            if (!IsEditingCustomRule) return;
            var domain = RuleDomain.Trim();
            if (string.IsNullOrWhiteSpace(domain)) throw new UserError("请输入域名或完整网址。");
            var originalIndex = _editingCustomRule is null ? -1 : CustomRules.IndexOf(_editingCustomRule);
            var draft = CustomRules.ToList();
            if (_editingCustomRule is not null && originalIndex < 0) throw new UserError("列表已变化，请取消编辑后重试。");
            var rule = new CustomRule(domain, RuleIncludeSubdomains, RuleTarget, RuleEnabled);
            if (originalIndex < 0) draft.Add(rule); else draft[originalIndex] = rule;
            var saved = await CommitCustomRulesAsync(draft, token);
            if (originalIndex < 0) CustomRules.Add(saved[^1]); else CustomRules[originalIndex] = saved[originalIndex];
            SelectedCustomRule = saved[originalIndex < 0 ? saved.Length - 1 : originalIndex];
            CancelCustomRuleEdit();
        }, available: () => IsEditingCustomRule);
        RefreshCommand = Command(token => RefreshAsync(true, token), false);
        PrimaryCommand = Command(async token =>
        {
            if (Active) { CancelAutoConnect(); await _connections.StopAsync(); Notice = "本助手分流已停止，接管前的代理已还原；其他软件接管的设置保持不变。"; }
            else if (!_managed) { Page = "settings"; WizardStep = 1; Notice = "请完成首次 VPN 设置，之后由助手自动准备配置。"; return; }
            else { await _connections.StartAsync(BuildSettings(true), token); Notice = "已启用分流，请查看当前链路检查结果。"; }
            await RefreshAsync(!Active, token);
        });
        StopCommand = Command(async token => { CancelAutoConnect(); await _connections.StopAsync(); await RefreshAsync(false, token); Notice = "已停止本助手分流；Windows 代理已还原或保留其他软件的接管。"; });
        SaveCommand = Command(async token => { _store.Save(BuildSettings(true)); _startup.SetEnabled(AutoStart); StartupStatus = _startup.Status; AutoStart = _startup.IsEnabled; Raise(nameof(StartupStatus)); Raise(nameof(AutoStart)); Notice = "设置已保存在本机，启用时自动管理 VPN、组件和当前连接代理。"; await Task.CompletedTask; });
        GenerateCommand = Command(ExportProfileAsync);
        ImportCommand = Command(async token =>
        {
            if (_connections.HasProxySession) throw new UserError("请先停止并还原当前分流，再重新准备配置。");
            var settings = BuildSettings(true);
            await _connections.StartAsync(settings, token);
            Notice = "独立配置已准备，代理已接管，请查看当前链路检查结果。"; WizardStep = 3;
            await RefreshAsync(true, token);
        });
        MullvadCommand = Command(async token => { await _connections.ConfigureMullvadAsync(BuildSettings(false), token); Notice = "Mullvad 已为随包直连组件配置排除项，未清空已有排除列表。"; });
        MigrateCommand = Command(async token =>
        {
            if (MessageBox.Show("将核验旧组件来源，停止识别出的旧版进程并启用随包组件，同时备份并移除本方案旧启动项。Windscribe 请先确认新组件排除项。迁移期间国内代理可能短暂中断。继续？", "迁移旧版直连组件", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _connections.MigrateLegacyAsync(BuildSettings(false), token); await RefreshAsync(false, token); Notice = "已切换到随包组件，旧版启动项如存在则已备份。";
        });
        OpenProviderCommand = Command(_ => { _environment.OpenProvider(Provider); return Task.CompletedTask; });
        OpenClashCommand = Command(_ => { _environment.OpenClash(); return Task.CompletedTask; });
        InstallServiceCommand = Command(_ => { _environment.InstallClashService(); Notice = "已打开官方服务安装器，请完成 Windows 权限提示后重新检测。"; return Task.CompletedTask; });
        OpenHelperLocationCommand = Command(_ => { if (!Directory.Exists(paths.HelperDirectory)) throw new UserError("组件目录缺失，请修复安装。"); ClientEnvironment.Launch(paths.HelperDirectory); return Task.CompletedTask; }, false);
        OpenStartupCommand = Command(_ => { ClientEnvironment.Launch("ms-settings:startupapps"); return Task.CompletedTask; }, false);
        OpenProxySettingsCommand = Command(_ => { ClientEnvironment.Launch("ms-settings:network-proxy"); return Task.CompletedTask; }, false);
        OpenDataCommand = Command(_ => { Directory.CreateDirectory(paths.DataDirectory); ClientEnvironment.Launch(paths.DataDirectory); return Task.CompletedTask; }, false);
        ExportDiagnosticsCommand = Command(ExportDiagnosticsAsync, false);
        CancelCommand = new UiCommand(() => { _operation?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> Providers { get; } = new[] { "Mullvad", "Windscribe" };
    public string AppVersion => typeof(DesktopViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.1";
    public bool IsSafeMode { get; }
    public string Page { get => _page; set => Set(ref _page, value); }
    public int WizardStep { get => _wizardStep; set => Set(ref _wizardStep, Math.Clamp(value, 0, 3)); }
    public int SettingsTab { get => _settingsTab; set => Set(ref _settingsTab, value); }
    public string Provider
    {
        get => _provider;
        set
        {
            if (value == _provider || value is not ("Mullvad" or "Windscribe")) return;
            if (_connections.HasProxySession) { Notice = "请先停止并还原当前分流，再切换 VPN 方案。"; Raise(); return; }
            _provider = value; _managed = false; WindscribeConfirmed = false; Raise(); Raise(nameof(WindscribeConfirmed)); Raise(nameof(PrimaryActionLabel));
        }
    }
    public string GatewayHost { get; set; } = "";
    public string GatewayPort { get; set; } = "61053";
    public string GatewayUsername { get; set; } = "";
    public string GatewayPassword { get; set; } = "";
    public string MixedPort { get; set; } = "7897";
    public bool AutoStart { get; set; }
    public bool AutoEnable { get; set; }
    public bool CloseToTray { get; set; } = true;
    private bool _windscribeConfirmed;
    public bool WindscribeConfirmed { get => _windscribeConfirmed; set { _windscribeConfirmed = value; _managed = Provider == "Mullvad" || value; Raise(); Raise(nameof(PrimaryActionLabel)); } }
    public bool AllowLan { get; set; } = true;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool Active { get => _active; private set { Set(ref _active, value); Raise(nameof(PrimaryActionLabel)); } }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Tone { get => _tone; private set => Set(ref _tone, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string PrimaryActionLabel => Active ? "停止并还原代理" : _managed ? "启用分流" : "开始设置";
    public string DomesticStatus { get; private set; } = "未检测";
    public string OverseasStatus { get; private set; } = "未检测";
    public string DomesticTone { get; private set; } = "muted";
    public string OverseasTone { get; private set; } = "muted";
    public string HelperVersion { get; private set; } = "正在读取";
    public string HelperLocation => _paths.HelperExe;
    public string ClashDescription { get; private set; } = "正在检查";
    public string ServiceStatus { get; private set; } = "正在检查";
    public string StartupStatus { get; private set; } = "正在检查";
    public IReadOnlyList<CheckItem> Checks { get; private set; } = [];
    public ObservableCollection<CustomRule> CustomRules { get; } = new();
    public CustomRule? SelectedCustomRule
    {
        get => _selectedCustomRule;
        set
        {
            if (ReferenceEquals(_selectedCustomRule, value)) return;
            _selectedCustomRule = value; Raise(); Raise(nameof(RuleToggleAction)); CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsEditingCustomRule { get => _isEditingCustomRule; private set { Set(ref _isEditingCustomRule, value); Raise(nameof(CustomRulePageTitle)); CommandManager.InvalidateRequerySuggested(); } }
    public string RuleDomain { get => _ruleDomain; set => Set(ref _ruleDomain, value); }
    public bool RuleIncludeSubdomains { get => _ruleSubdomains; set => Set(ref _ruleSubdomains, value); }
    public bool RuleEnabled { get => _ruleEnabled; set => Set(ref _ruleEnabled, value); }
    public string RuleTarget { get => _ruleTarget; set { if (value is "ChinaDirect" or "VPN" or "REJECT") Set(ref _ruleTarget, value); } }
    public string RuleToggleAction => SelectedCustomRule?.Enabled == true ? "停用" : "启用";
    public string CustomRulePageTitle => !IsEditingCustomRule ? "自定义规则" : _editingCustomRule is null ? "添加规则" : "编辑规则";
    public string CustomRuleSummary => $"{CustomRules.Count} 条规则，{CustomRules.Count(rule => rule.Enabled)} 条启用";
    public ICommand AddCustomRuleCommand { get; }
    public ICommand EditCustomRuleCommand { get; }
    public ICommand CancelCustomRuleEditCommand { get; }
    public ICommand RemoveCustomRuleCommand { get; }
    public ICommand ToggleCustomRuleCommand { get; }
    public ICommand MoveCustomRuleUpCommand { get; }
    public ICommand MoveCustomRuleDownCommand { get; }
    public ICommand SaveCustomRulesCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand GenerateCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand MullvadCommand { get; }
    public ICommand MigrateCommand { get; }
    public ICommand OpenProviderCommand { get; }
    public ICommand OpenClashCommand { get; }
    public ICommand InstallServiceCommand { get; }
    public ICommand OpenHelperLocationCommand { get; }
    public ICommand OpenStartupCommand { get; }
    public ICommand OpenProxySettingsCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand CancelCommand { get; }
    public async Task InitializeAsync()
    {
        await RunAsync(async token =>
        {
            HelperVersion = _helper.Manifest is { } manifest ? $"Mihomo {manifest.Version} · 随应用提供" : "组件缺失，请修复安装";
            ClashDescription = File.Exists(_paths.MainCoreExe) ? "Mihomo 主内核与规则库随应用提供" : "主内核文件缺失，请修复安装";
            ServiceStatus = "由助手启动和维护，无需安装 Clash 服务";
            StartupStatus = _startup.Status; AutoStart = _startup.IsEnabled;
            foreach (var property in new[] { nameof(HelperVersion), nameof(ClashDescription), nameof(ServiceStatus), nameof(StartupStatus), nameof(AutoStart) }) Raise(property);
            await RefreshAsync(false, token);
        }, false);
    }
    public Task RefreshBackgroundAsync()
    {
        if (IsBusy || IsSafeMode || !_backgroundRefresh.IsCompleted || !_connections.HasProxySession)
            return Task.CompletedTask;
        return _backgroundRefresh = RunAsync(async token =>
        {
            await _connections.MaintainAsync(BuildSettings(false), token);
            await RefreshAsync(false, token);
        }, background: true);
    }
    private async Task RefreshAsync(bool networkChecks, CancellationToken token)
    {
        var report = await _connections.RefreshAsync(BuildSettings(false), networkChecks && !IsSafeMode, token);
        Headline = report.Headline; Subtitle = report.Subtitle; Tone = report.Tone; Active = report.Active;
        DomesticStatus = report.DomesticStatus; OverseasStatus = report.OverseasStatus; DomesticTone = DomesticStatus is "正常" or "已就绪" ? "good" : "muted"; OverseasTone = OverseasStatus is "正常" or "可用" ? "good" : "attention";
        _managed = Provider == "Mullvad" || WindscribeConfirmed;
        if (!Checks.SequenceEqual(report.Checks))
        {
            Checks = report.Checks;
            Raise(nameof(Checks));
        }
        foreach (var property in new[] { nameof(DomesticStatus), nameof(OverseasStatus), nameof(DomesticTone), nameof(OverseasTone), nameof(PrimaryActionLabel) }) Raise(property);
    }
    private AppSettings BuildSettings(bool validate)
    {
        if (!int.TryParse(MixedPort, out var port)) throw new UserError("未能选择可用的本地代理端口，请关闭占用端口的软件后重试。");
        var settings = new AppSettings { Provider = Provider, MixedPort = port, AutoEnable = AutoEnable, CloseToTray = CloseToTray, WindscribeConfirmed = WindscribeConfirmed, AllowLan = AllowLan, WindscribeInterface = string.IsNullOrEmpty(_connections.BoundInterface) ? _savedInterface : _connections.BoundInterface };
        if (validate) ProfileService.ValidateStandalone(settings); return settings;
    }
    private async Task ExportProfileAsync(CancellationToken token)
    {
        var settings = BuildSettings(true);
        if (File.Exists(_paths.MainConfig) && ProfileService.ReadStandaloneSettings(_paths.MainConfig).Provider == Provider) settings = ProfileService.ReadStandaloneSettings(_paths.MainConfig);
        else
        {
            if (Provider == "Windscribe") settings.WindscribeInterface = ConnectionService.DetectWindscribeInterface() ?? throw new UserError("请先连接 Windscribe IKEv2 或 WireGuard，识别 VPN 接口后再导出配置。");
        }
        var yaml = _profiles.RenderStandalone(settings);
        var exported = ProfileService.Parse(yaml); exported.Children.Remove(new YamlDotNet.RepresentationModel.YamlScalarNode("external-controller-pipe")); yaml = ProfileService.Dump(exported);
        var dialog = new SaveFileDialog { FileName = $"Clash-{Provider}.local.yaml", Filter = "Clash YAML 配置|*.yaml", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        token.ThrowIfCancellationRequested();
        SettingsStore.AtomicWrite(dialog.FileName, yaml);
        Notice = "配置已导出，DNS 已包含在 YAML 中；Windscribe 配置绑定最近识别的 VPN 接口。";
        await Task.CompletedTask;
    }
    private async Task ExportDiagnosticsAsync(CancellationToken token)
    {
        var dialog = new SaveFileDialog { FileName = "SplitHelper-diagnostics.json", Filter = "脱敏诊断|*.json" };
        if (dialog.ShowDialog() != true) return;
        var report = new { version = AppVersion, provider = Provider, safeMode = IsSafeMode, helperVersion = HelperVersion, clash = ClashDescription, service = ServiceStatus, startup = StartupStatus, checks = Checks, credentials = "excluded", addresses = "excluded", helperPath = "excluded" };
        token.ThrowIfCancellationRequested(); SettingsStore.AtomicWrite(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); Notice = "脱敏诊断已导出，不包含网关凭据和实际地址。"; await Task.CompletedTask;
    }
    private void BeginCustomRuleEdit(CustomRule? rule)
    {
        _editingCustomRule = rule;
        RuleDomain = rule?.Domain ?? ""; RuleIncludeSubdomains = rule?.IncludeSubdomains ?? true;
        RuleTarget = rule?.Target ?? "VPN"; RuleEnabled = rule?.Enabled ?? true;
        IsEditingCustomRule = true;
        Notice = "";
    }
    private void CancelCustomRuleEdit()
    {
        _editingCustomRule = null; IsEditingCustomRule = false;
        RuleDomain = ""; RuleIncludeSubdomains = true; RuleTarget = "VPN"; RuleEnabled = true;
    }
    private async Task<CustomRule[]> CommitCustomRulesAsync(IReadOnlyList<CustomRule> rules, CancellationToken token)
    {
        var snapshot = ProfileService.NormalizeCustomRules(rules);
        var applied = await _connections.SaveCustomRulesAsync(snapshot, token);
        Notice = applied ? "规则已保存，内核回读确认已应用。请重新打开异常网页；已有连接可能继续使用旧路径。" : "规则已保存，下次启用分流时生效。";
        return snapshot;
    }
    private async Task MoveCustomRuleAsync(int offset, CancellationToken token)
    {
        if (SelectedCustomRule is not { } rule) return;
        var index = CustomRules.IndexOf(rule); var next = index + offset;
        if (next < 0 || next >= CustomRules.Count) return;
        var draft = CustomRules.ToList(); (draft[index], draft[next]) = (draft[next], draft[index]);
        await CommitCustomRulesAsync(draft, token);
        CustomRules.Move(index, next);
        SelectedCustomRule = rule;
    }
    private UiCommand Command(Func<CancellationToken, Task> action, bool mutates = true, Func<bool>? available = null) => new(() => RunAsync(action, mutates), () => !IsBusy && (!mutates || !IsSafeMode) && (available?.Invoke() ?? true));
    private async Task RunAsync(Func<CancellationToken, Task> action, bool mutates = true, bool background = false)
    {
        if (IsBusy || (mutates && IsSafeMode)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (background) _backgroundOperation = operation;
        else
        {
            _operation = operation;
            IsBusy = true;
        }
        try
        {
            // Queue foreground work behind any running maintenance without disabling commands on each tick.
            if (!background) await _backgroundRefresh.WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            await action(operation.Token);
        }
        catch (UserError error) { Notice = error.Message; Headline = "操作需要处理"; Subtitle = error.Message; Tone = "attention"; }
        catch (OperationCanceledException) when (background && operation.IsCancellationRequested) { }
        catch (OperationCanceledException) { Notice = "操作已取消；未完成的变更会回退，规则恢复失败时会保留待恢复记录。"; Headline = "操作已取消"; Tone = "attention"; }
        catch (Exception error)
        {
            Notice = "操作未完成，请查看本机诊断信息（" + error.GetType().Name + "）。"; Headline = "操作需要处理"; Tone = "attention";
            try { Directory.CreateDirectory(_paths.DataDirectory); var log = Path.Combine(_paths.DataDirectory, "app-errors.log"); if (File.Exists(log) && new FileInfo(log).Length > 1024 * 1024) File.Delete(log); File.AppendAllText(log, DateTimeOffset.Now.ToString("O") + " " + error.GetType().FullName + "\n" + error.StackTrace + "\n"); } catch (IOException) { }
        }
        finally
        {
            if (background) _backgroundOperation = null;
            else
            {
                _operation = null;
                IsBusy = false;
            }
        }
    }
    public async Task RunAutoConnectAsync()
    {
        if (!AutoEnable || IsSafeMode) return;
        _autoConnect = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _autoConnect.CancelAfter(TimeSpan.FromMinutes(10));
        var autoToken = _autoConnect.Token;
        try
        {
            while (!autoToken.IsCancellationRequested)
            {
                if (!IsBusy)
                {
                    autoToken.ThrowIfCancellationRequested();
                    if (IsBusy) { await Task.Delay(1000, autoToken); continue; }
                    await RunAsync(async token => { using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, autoToken); await _connections.StartAsync(BuildSettings(true), operation.Token, resumePreviousSession: true); await RefreshAsync(true, operation.Token); Notice = "官方 VPN 与内置组件已就绪，分流已自动启用。"; });
                    if (Active) return;
                }
                await Task.Delay(15000, autoToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Notice = "自动启用尚未完成，请打开连接向导检查设置。"; }
    }
    private void CancelAutoConnect() => _autoConnect?.Cancel();
    public async Task<bool> PrepareExitAsync()
    {
        CancelAutoConnect(); _operation?.Cancel(); _backgroundOperation?.Cancel();
        for (var attempt = 0; (IsBusy || !_backgroundRefresh.IsCompleted) && attempt < 100; attempt++) await Task.Delay(100);
        if (IsBusy || !_backgroundRefresh.IsCompleted) { Notice = "仍在取消当前操作，请稍后再退出。"; return false; }
        if (IsSafeMode) return true;
        try { await _connections.StopAsync(); return true; }
        catch (Exception error) { Notice = error is UserError ? error.Message : "尚未完成代理还原，请重试停止后再退出。"; return false; }
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Raise(property); }
    private void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public void Dispose() { _lifetime.Cancel(); _autoConnect?.Dispose(); _operation?.Cancel(); _connections.Dispose(); _lifetime.Dispose(); }
}

public sealed class UiCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(); }
}
