using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SplitHelper.Services;

public sealed class ConnectionService(AppPaths paths, ClientEnvironment environment, SettingsStore store, ProfileService profiles, HelperManager helper, WindowsProxy proxy, StartupService startup, MihomoClient core) : IDisposable
{
    private readonly MainCoreManager _main = new(paths, core);
    private bool _vpnWasUp;
    private bool _actualExitUnverified = true;
    private const string DomainTrace = "https://www.cloudflare.com/cdn-cgi/trace";
    private const string AddressTrace = "https://1.1.1.1/cdn-cgi/trace";
    private const string DomesticProbe = "https://www.baidu.com/";
    private DateTime _lastReconnectAttempt;
    public int DetectedPort { get; private set; }
    public bool HasProxySession => proxy.HasSession;
    public string MainVersion => _main.Version;
    public string BoundInterface { get; private set; } = "";
    public static string? DetectWindscribeInterface()
    {
        // Use the selected Internet route, not a disconnected adapter left in the inventory.
        if (GetBestInterface(0x01010101, out var index) != 0) return null;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.Name is not ("Windscribe IKEv2" or "WindscribeWireguard") || adapter.OperationalStatus != OperationalStatus.Up) continue;
            try { if (adapter.GetIPProperties().GetIPv4Properties()?.Index == index) return adapter.Name; }
            catch (NetworkInformationException) { }
        }
        return null;
    }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destination, out uint index);
    private static string? RasConnection(string? vpnInterface) => vpnInterface == "Windscribe IKEv2" ? vpnInterface : null;
    private string? ManagementIssue => File.Exists(Path.Combine(environment.ClashHome, "verge.yaml")) ? profiles.ProxyManagementIssue() : null;

    public async Task ConfigureMullvadAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await helper.VerifyAsync(cancellationToken);
        var cli = environment.MullvadCli ?? throw new UserError("未找到 Mullvad 官方客户端，请先安装并登录。");
        List<string[]> commands = [["split-tunnel", "set", "on"], ["split-tunnel", "app", "add", paths.HelperExe]];
        if (settings.AllowLan) commands.Add(["lan", "set", "allow"]);
        foreach (var arguments in commands)
            if ((await ClientEnvironment.RunAsync(cli, arguments, cancellationToken)).ExitCode != 0) throw new UserError("Mullvad 未能配置直连组件排除项，请在官方客户端检查分流设置。");
        var check = await ClientEnvironment.RunAsync(cli, ["split-tunnel", "get"], cancellationToken);
        if (check.ExitCode != 0 || !check.Output.Contains(paths.HelperExe, StringComparison.OrdinalIgnoreCase)) throw new UserError("Mullvad 排除项读回校验未通过。");
    }
    private async Task ConnectVpnAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.Provider == "Windscribe")
        {
            if (!settings.WindscribeConfirmed) throw new UserError("首次使用请在 Windscribe 将内置直连组件加入 Exclusive 排除列表，然后勾选确认。");
            if (DetectWindscribeInterface() is not null) return;
            var cli = environment.WindscribeCli ?? throw new UserError("未找到 Windscribe 官方客户端，请先安装并登录。");
            _lastReconnectAttempt = DateTime.UtcNow;
            if ((await ClientEnvironment.RunAsync(cli, ["connect", "-n"], token)).ExitCode != 0) throw new UserError("Windscribe 未能连接，请打开官方客户端检查登录状态。");
            for (var i = 0; i < 80; i++) { if (DetectWindscribeInterface() is not null) return; await Task.Delay(500, token); }
            throw new UserError("等待 Windscribe 连接超时，请在官方客户端检查网络或登录状态；当前支持 IKEv2 和 WireGuard。");
        }
        var mullvad = environment.MullvadCli ?? throw new UserError("未找到 Mullvad 官方客户端。");
        if ((await ClientEnvironment.RunAsync(mullvad, ["connect"], token)).ExitCode != 0) throw new UserError("Mullvad 未能连接，请检查登录状态。");
    }

    public async Task<bool> SaveCustomRulesAsync(IReadOnlyList<CustomRule> rules, CancellationToken token)
    {
        var candidate = ProfileService.NormalizeCustomRules(rules);
        if (File.Exists(paths.PendingCustomRulesFile)) throw new UserError("自定义规则恢复待处理，请等待分流恢复或重新启用后再保存。");
        var committed = profiles.LoadCustomRules();
        var json = ProfileService.SerializeCustomRules(candidate);
        token.ThrowIfCancellationRequested();
        if (!proxy.HasSession)
        {
            if (_main.HasOwnProcess) throw new UserError("本助手主内核仍在运行。请先停止分流或恢复代理接管后再保存自定义规则。");
            SettingsStore.AtomicWrite(paths.CustomRulesFile, json);
            return false;
        }

        var settings = ProfileService.ReadStandaloneSettings(paths.MainConfig);
        if (proxy.Session?.Port != settings.MixedPort || !_main.OwnListenerReady(settings.MixedPort))
            throw new UserError("本助手的代理会话与主内核尚未就绪，请等待分流恢复后再保存自定义规则。");
        var previousYaml = File.ReadAllText(paths.MainConfig);
        token.ThrowIfCancellationRequested();
        SettingsStore.AtomicWrite(paths.PendingCustomRulesFile, json);
        var reloadStarted = false;
        var reloadAcknowledged = false;
        try
        {
            profiles.PrepareStandalone(settings, candidate);
            token.ThrowIfCancellationRequested();
            // Once a PUT can reach the core, finish checking it independently of UI cancellation.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            reloadStarted = true;
            var applied = await _main.ReloadPreparedConfigAsync(deadline.Token);
            reloadAcknowledged = true;
            await _main.VerifyCustomRulesAsync(applied, candidate, deadline.Token);
            token.ThrowIfCancellationRequested();
            File.Move(paths.PendingCustomRulesFile, paths.CustomRulesFile, overwrite: true);
        }
        catch (Exception error)
        {
            // Mihomo parses before taking its apply lock. A PUT without a response can apply late
            // even after another PUT succeeds. Keep the marker so recovery restarts that process.
            var reloadUnconfirmed = reloadStarted && !reloadAcknowledged
                && (error is OperationCanceledException or IOException or HttpRequestException { StatusCode: null });
            try
            {
                SettingsStore.AtomicWrite(paths.MainConfig, previousYaml);
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var restored = await _main.ReloadPreparedConfigAsync(rollback.Token);
                await _main.VerifyCustomRulesAsync(restored, committed, rollback.Token);
                if (!reloadUnconfirmed) File.Delete(paths.PendingCustomRulesFile);
            }
            catch (Exception)
            {
                throw new UserError("自定义规则未保存，运行中的规则尚未确认恢复。已保留上次保存内容，恢复待处理。");
            }
            if (reloadUnconfirmed) throw new UserError("主内核未确认完成本次重载。自定义规则未保存，恢复待处理。");
            token.ThrowIfCancellationRequested();
            throw new UserError("自定义规则未保存，已恢复上次保存的规则。");
        }
        var (traceUrl, probeDomesticProxy) = SelectProbeRoutes(candidate);
        if (traceUrl == AddressTrace || probeDomesticProxy) _actualExitUnverified = true;
        return true;
    }

    private async Task RecoverCustomRulesAsync(AppSettings settings, CancellationToken token)
    {
        var committed = profiles.LoadCustomRules();
        profiles.PrepareStandalone(settings, committed);
        // A component read/verification failure must not stop a still-serving owned core.
        await _main.VerifyAsync(token);
        token.ThrowIfCancellationRequested();
        // A fresh owned process cannot finish a candidate PUT from before the crash or timeout.
        _main.StopOwn();
        await _main.StartAsync(settings, token);
        await _main.VerifyCustomRulesAsync(settings, committed, token);
        File.Delete(paths.PendingCustomRulesFile);
    }

    public async Task StartAsync(AppSettings settings, CancellationToken token, bool resumePreviousSession = false)
    {
        ProfileService.ValidateStandalone(settings);
        if (ManagementIssue is { } issue) throw new UserError(issue);
        if (resumePreviousSession && proxy.HasSession)
        {
            // This call did not acquire the existing proxy session; failure must keep its protection.
            await MaintainAsync(settings, token);
            if (settings.Provider == "Mullvad") await ConnectVpnAsync(settings, token);
            await VerifyPipelineAsync(settings, token);
            return;
        }
        if (proxy.HasSession && (!resumePreviousSession || proxy.Session?.Port != settings.MixedPort)) await StopAsync();
        if (settings.Provider == "Mullvad") await ConfigureMullvadAsync(settings, token);
        if (settings.Provider == "Windscribe" && !settings.WindscribeConfirmed) throw new UserError("请先确认 Windscribe 已排除内置直连组件。");
        try
        {
            await helper.StartAsync(token);
            await ConnectVpnAsync(settings, token);
            if (settings.Provider == "Windscribe") settings.WindscribeInterface = DetectWindscribeInterface() ?? throw new UserError("Windscribe 接口尚未就绪，请重试。");
            BoundInterface = settings.WindscribeInterface;
            if (File.Exists(paths.PendingCustomRulesFile)) await RecoverCustomRulesAsync(settings, token);
            else
            {
                profiles.PrepareStandalone(settings);
                await _main.StartAsync(settings, token);
                await _main.ReloadAsync(token);
            }
            (string TraceUrl, string? OverseasIp) exits;
            // A network adapter can appear before the VPN finishes establishing its routes.
            for (var attempt = 0; ; attempt++)
            {
                try { exits = await VerifyPipelineAsync(settings, token); break; }
                catch (UserError) when (attempt < 2) { await Task.Delay(750, token); }
            }
            store.Save(settings);
            proxy.Enable(settings.MixedPort, settings.Provider == "Windscribe" ? RasConnection(BoundInterface) : null);
            if (!proxy.IsActive) throw new UserError("Windows 当前活动连接没有使用助手代理。");
            await VerifyEffectiveProxyAsync(settings, exits, token);
            _vpnWasUp = true;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }
    public async Task MaintainAsync(AppSettings settings, CancellationToken token)
    {
        if (!proxy.HasSession) return;
        if (ManagementIssue is { } issue) throw new UserError(issue);
        var session = proxy.Session!;
        // Config is written before preferences; recover its route parameters after an interrupted rebind.
        var prepared = ProfileService.ReadStandaloneSettings(paths.MainConfig);
        settings.Provider = prepared.Provider; settings.MixedPort = prepared.MixedPort; settings.WindscribeInterface = prepared.WindscribeInterface;
        if (session.Port != settings.MixedPort) throw new UserError("存在旧版本代理记录，请先停止并还原，再启用新主内核。");
        if (File.Exists(paths.PendingCustomRulesFile))
        {
            await RecoverCustomRulesAsync(prepared, token);
            _vpnWasUp = false;
        }
        var vpnInterface = settings.Provider == "Windscribe" ? DetectWindscribeInterface() : null;
        proxy.Enable(settings.MixedPort, RasConnection(vpnInterface));
        if (!helper.OwnListenerReady) await helper.StartAsync(token);
        if (!_main.OwnListenerReady(settings.MixedPort))
        {
            profiles.PrepareStandalone(settings);
            await _main.StartAsync(settings, token);
            _vpnWasUp = false;
        }
        if (settings.Provider != "Windscribe")
        {
            _ = _main.CheckGeoUpdateAsync();
            return;
        }
        if (vpnInterface is null)
        {
            _vpnWasUp = false;
            if (DateTime.UtcNow - _lastReconnectAttempt > TimeSpan.FromSeconds(30)) await ConnectVpnAsync(settings, token);
            vpnInterface = DetectWindscribeInterface();
        }
        if (vpnInterface is not null && (!_vpnWasUp || BoundInterface != vpnInterface))
        {
            _vpnWasUp = false;
            proxy.Enable(settings.MixedPort, RasConnection(vpnInterface));
            settings.WindscribeInterface = vpnInterface;
            profiles.PrepareStandalone(settings);
            BoundInterface = vpnInterface;
            await _main.ReloadAsync(token);
            await VerifyPipelineAsync(settings, token);
            store.Save(settings);
            _vpnWasUp = true;
        }
        if (_vpnWasUp) _ = _main.CheckGeoUpdateAsync();
    }
    public Task StopAsync(bool requireComponentsStopped = false)
    {
        var previousPort = proxy.Session?.Port;
        if (!proxy.Restore()) proxy.ArchiveExternalSession();
        if (proxy.ReferencesAny(_main.OwnedListenerPorts())) throw new UserError("仍有默认或 VPN 代理引用主内核，已保留组件；请先处理诊断中的代理冲突。");
        _main.StopOwn();
        _actualExitUnverified = false;
        var dependencyPorts = new List<int> { 18997 };
        if (previousPort is > 0) dependencyPorts.Add(previousPort.Value);
        // A restored Clash proxy can still depend on the same domestic helper.
        if (proxy.ReferencesLocalProxy())
        {
            var legacy = Path.Combine(environment.ClashHome, "clash-verge.yaml");
            try
            {
                if (File.Exists(legacy) && int.TryParse(ProfileService.Value(ProfileService.Parse(File.ReadAllText(legacy)), "mixed-port"), out var legacyPort)) dependencyPorts.Add(legacyPort);
            }
            catch (Exception error) when (error is UserError or IOException or YamlDotNet.Core.YamlException)
            {
                _vpnWasUp = false;
                if (requireComponentsStopped) throw new UserError("仍有本机代理，但旧 Clash 配置无法读取；已保留直连组件，请关闭该代理后重试更新或卸载。");
                return Task.CompletedTask;
            }
        }
        if (!proxy.ReferencesAny(dependencyPorts.ToArray())) helper.StopOwn();
        else if (requireComponentsStopped) throw new UserError("原代理仍使用直连组件，请先关闭或切换默认和 VPN 连接代理，再更新或卸载。");
        _vpnWasUp = false;
        return Task.CompletedTask;
    }
    public async Task<HealthReport> RefreshAsync(AppSettings settings, bool networkChecks, CancellationToken token)
    {
        var checks = new List<CheckItem>();
        var direct = helper.OwnListenerReady;
        checks.Add(new("内置直连组件", direct ? "随包 helper 已就绪，国内连接经本地宽带" : "启用分流时由助手自动启动", direct ? "good" : "muted"));
        CoreStatus? status = null;
        if (_main.OwnListenerReady(settings.MixedPort))
            try { status = await core.StatusAsync(token); DetectedPort = status.Port; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        checks.Add(new("内置主内核", status is null ? "随应用提供，启用时自动准备配置与规则" : $"Mihomo {status.Version} · 端口 {status.Port} · TUN / IPv6 关闭", status is null ? "muted" : "good"));
        checks.Add(_main.GeoUpdateStatus());
        var active = proxy.IsActive;
        checks.Add(new("默认与 VPN 连接代理", active ? "逐连接设置与当前 Windows 有效代理均指向助手" : proxy.HasSession ? "代理设置发生变化，已保留还原记录" : "尚未接管，启用时自动配置", active ? "good" : proxy.HasSession ? "attention" : "muted"));
        var issue = ManagementIssue;
        checks.Add(new("代理管理归属", issue ?? "由分流助手统一管理；未发现 Clash 系统代理、PAC 或代理守卫冲突", issue is null ? "good" : "attention"));
        var vpnInterface = DetectWindscribeInterface();
        var up = settings.Provider != "Windscribe" || vpnInterface is not null;
        checks.Add(new("官方 VPN", settings.Provider == "Windscribe" ? up ? $"{vpnInterface} 已连接，无需本地 SOCKS 网关" : "VPN 尚未连接；启用后由助手连接和重连（IKEv2 / WireGuard）" : "使用 Mullvad 官方固定 SOCKS 出口", up ? "good" : "attention"));
        if (networkChecks && status is not null && direct)
        {
            try
            {
                await VerifyPipelineAsync(settings, token);
                if (!_actualExitUnverified) checks.Add(new("实际访问路径", "国内宽带与海外 VPN 出口分离，直连出站可达", "good"));
            }
            catch (Exception e) when (e is UserError or HttpRequestException or IOException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); return new("访问链路需要处理", e is UserError ? e.Message : "代理访问检测未通过，海外连接不会自动切换到宽带。", "attention", active, direct ? "已就绪" : "未启动", "待处理", checks); }
        }
        if (issue is not null) return new("代理管理需要处理", issue, "attention", active, direct ? "已就绪" : "未启动", "待处理", checks);
        if (active && (status is null || !direct)) return new("内置组件需要恢复", "代理接管记录已保留，正在恢复内置组件。", "attention", true, direct ? "已就绪" : "待恢复", "待恢复", checks);
        if (active && !up) return new("VPN 已断开，正在恢复", "国内直连继续工作；海外请求保持经 VPN，助手会自动重连。", "attention", true, direct ? "正常" : "待处理", "已阻断", checks);
        if (active && settings.Provider == "Windscribe" && (!_vpnWasUp || BoundInterface != vpnInterface)) return new("正在验证 VPN 恢复", "正在更新 VPN 接口绑定并验证国内与海外出口。", "attention", true, direct ? "正常" : "待处理", "待验证", checks);
        if (active && _actualExitUnverified)
        {
            const string detail = "可在诊断页重新验证国内与海外实际出口。";
            checks.Add(new("实际访问路径", "实际出口未验证", "attention"));
            return new("分流已启用", detail, "attention", true, "正常", "实际出口未验证", checks);
        }
        return new(active ? "分流已启用" : "准备好后，一键连接", active ? "助手统一管理主内核、直连组件和当前连接代理。" : "选择官方 VPN，完成首次排除项设置，之后交给助手。", active ? "good" : "muted", active, direct ? "正常" : "未启动", active && up ? "可用" : "未检测", checks);
    }

    internal static (string TraceUrl, bool TestDomesticProxy) SelectProbeRoutes(IReadOnlyList<CustomRule> rules)
    {
        string? FirstTarget(string host) => rules.FirstOrDefault(rule => rule.Enabled && (rule.Domain == host
            || rule.IncludeSubdomains && host.EndsWith("." + rule.Domain, StringComparison.Ordinal)))?.Target;
        var traceTarget = FirstTarget("www.cloudflare.com");
        var domesticTarget = FirstTarget("www.baidu.com");
        return (traceTarget is null or "VPN" ? DomainTrace : AddressTrace, domesticTarget is not (null or "ChinaDirect"));
    }

    private async Task<(string TraceUrl, string? OverseasIp)> VerifyPipelineAsync(AppSettings settings, CancellationToken token)
    {
        if (!helper.OwnListenerReady || !_main.OwnListenerReady(settings.MixedPort)) throw new UserError("内置组件未就绪，请重新启用分流。");
        var status = await core.StatusAsync(token);
        if (status.Port != settings.MixedPort || !status.RuleMode || !status.Ipv6Off || !status.TunOff || !await core.StandaloneRoutingMatchesAsync(settings.Provider, token)) throw new UserError("主内核实际配置与预期不一致，请停止后重新启用。");
        if (settings.Provider == "Windscribe" && DetectWindscribeInterface() != settings.WindscribeInterface) throw new UserError("Windscribe 已断开或正在切换接口，海外请求仍绑定 VPN 出口。");
        var (traceUrl, testDomesticProxy) = SelectProbeRoutes(profiles.LoadCustomRules());
        var allowTraceRedirects = traceUrl == DomainTrace;
        var localProxy = new Uri($"http://127.0.0.1:{settings.MixedPort}");
        try
        {
            string? overseas;
            try
            {
                var domestic = TraceIp(await ProbeAsync(new Uri("socks5://127.0.0.1:18997"), null, traceUrl, token, allowTraceRedirects));
                overseas = TraceIp(await ProbeAsync(localProxy, null, traceUrl, token, allowTraceRedirects));
                if (domestic is null || overseas is null || domestic == overseas) throw new UserError("国内与海外出口未确认分离，请检查 helper 排除项；助手没有切换为直连。");
                if (settings.Provider == "Mullvad" && overseas != TraceIp(await ProbeAsync(new Uri("socks5://10.64.0.1:1080"), null, traceUrl, token, allowTraceRedirects))) throw new UserError("海外实际出口与 Mullvad 官方 SOCKS 不一致。");
            }
            catch (Exception error) when (traceUrl == AddressTrace && IsProbeFailure(error))
            {
                token.ThrowIfCancellationRequested();
                await VerifyVpnReachabilityAsync(settings, token);
                overseas = null;
            }
            if (testDomesticProxy) await core.ProbeProxyAsync("ChinaDirect", DomesticProbe, token);
            else await ProbeAsync(localProxy, null, DomesticProbe, token);
            _actualExitUnverified = overseas is null;
            if (traceUrl == AddressTrace && overseas is not null && proxy.IsActive)
                await VerifyEffectiveProxyAsync(settings, (traceUrl, overseas), token);
            return (traceUrl, overseas);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new UserError("实际代理访问未通过，请检查官方 VPN 与 helper 排除项；海外路径未回退到宽带。"); }
    }

    private async Task VerifyEffectiveProxyAsync(AppSettings settings, (string TraceUrl, string? OverseasIp) exits, CancellationToken token)
    {
        if (exits.OverseasIp is null) return;
        try
        {
            var address = new Uri("http://" + WindowsProxy.ReadEffective().Server);
            var effective = await ProbeAsync(address, null, exits.TraceUrl, token, allowRedirects: exits.TraceUrl == DomainTrace);
            if (TraceIp(effective) != exits.OverseasIp) throw new UserError("当前 Windows 代理的实际出口不一致，已取消启用。");
        }
        catch (Exception error) when (exits.TraceUrl == AddressTrace && IsProbeFailure(error))
        {
            token.ThrowIfCancellationRequested();
            _actualExitUnverified = true;
            await VerifyVpnReachabilityAsync(settings, token);
        }
    }

    private Task VerifyVpnReachabilityAsync(AppSettings settings, CancellationToken token)
        => core.ProbeProxyAsync(ProfileService.StandaloneVpnProxy(settings.Provider), DomainTrace, token);

    private static bool IsProbeFailure(Exception error) => error is UserError or HttpRequestException or IOException or OperationCanceledException;

    private static string? TraceIp(string text) => text.Split('\n').FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim();

    public static async Task<string> ProbeAsync(Uri proxyAddress, NetworkCredential? credentials, string url, CancellationToken cancellationToken, bool allowRedirects = true)
    {
        using var handler = new SocketsHttpHandler { UseProxy = true, Proxy = new WebProxy(proxyAddress) { Credentials = credentials }, ConnectTimeout = TimeSpan.FromSeconds(7), AllowAutoRedirect = allowRedirects };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(9), MaxResponseContentBufferSize = 1024 * 1024 };
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync(cancellationToken);
    }
    public async Task MigrateLegacyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await helper.VerifyAsync(cancellationToken);
        var foreign = helper.Instances().Where(instance => !helper.IsOwn(instance)).ToList();
        if (foreign.Count != 1 || string.IsNullOrEmpty(foreign[0].Path)) throw new UserError("未找到唯一且可识别的旧版组件，本次未停止任何程序。");
        var legacy = foreign[0]; var directory = Path.GetDirectoryName(legacy.Path)!;
        if (!File.Exists(Path.Combine(directory, "Start-Helper.ps1")) || !File.Exists(Path.Combine(directory, "direct.yaml"))) throw new UserError("该组件无法确认属于旧版方案，本次没有结束它。");
        var installedCore = Path.Combine(environment.ClashDirectory, "verge-mihomo.exe");
        if (!File.Exists(installedCore)) throw new UserError("无法核验旧版组件来源，请先确认 Clash 安装完整。");
        using (var old = File.OpenRead(legacy.Path)) using (var installed = File.OpenRead(installedCore))
        {
            if (!SHA256.HashData(old).SequenceEqual(SHA256.HashData(installed))) throw new UserError("旧版组件与本机官方内核校验不一致，本次没有结束它。");
        }
        if (settings.Provider == "Mullvad") await ConfigureMullvadAsync(settings, cancellationToken);
        else if (!settings.WindscribeConfirmed) throw new UserError("请先在 Windscribe 将随包组件加入排除项并确认，再迁移。");
        HelperManager.StopIdentity(legacy);
        try { await helper.StartAsync(cancellationToken); startup.RemoveLegacyEntry(directory); }
        catch
        {
            helper.StopOwn();
            var start = new ProcessStartInfo(legacy.Path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            start.ArgumentList.Add("-d"); start.ArgumentList.Add(Path.Combine(directory, "runtime")); start.ArgumentList.Add("-f"); start.ArgumentList.Add(Path.Combine(directory, "direct.yaml"));
            Process.Start(start)?.Dispose();
            throw;
        }
    }
    public void Dispose() { _main.Dispose(); core.Dispose(); helper.Dispose(); }
}
