using System.Net.NetworkInformation;
using System.Security.Cryptography;
using YamlDotNet.RepresentationModel;

namespace SplitHelper.Services;

public sealed record MainCoreManifest(string Version, string Sha256, string GeositeSha256, string CountrySha256);

public sealed class MainCoreManager(AppPaths paths, MihomoClient client) : IDisposable
{
    private static readonly string[] GeoDatabases = ["geosite.dat", "Country.mmdb"];
    private static readonly TimeSpan GeoUpdateInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan GeoRetryInterval = TimeSpan.FromMinutes(30);
    private Process? _launchedProcess;
    private DateTime? _nextGeoUpdateUtc;
    private Task _geoUpdate = Task.CompletedTask;
    private bool _geoUpdateFailed;
    private string IdentityFile => Path.Combine(paths.MainRuntimeDirectory, "process.json");
    private string GeoUpdateReceipt => Path.Combine(paths.MainRuntimeDirectory, "geo-update.ok");
    public string Version { get; private set; } = "";

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        MainCoreManifest? manifest;
        try { manifest = File.Exists(paths.MainManifest) ? JsonSerializer.Deserialize<MainCoreManifest>(File.ReadAllText(paths.MainManifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null; }
        catch (JsonException) { throw new UserError("内置主内核清单损坏，请修复安装。"); }
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version)) throw new UserError("内置主内核不完整，请使用正式安装包修复安装。");
        foreach (var (file, expected) in new[] { (paths.MainCoreExe, manifest.Sha256), (Path.Combine(paths.MainCoreDirectory, "geosite.dat"), manifest.GeositeSha256), (Path.Combine(paths.MainCoreDirectory, "Country.mmdb"), manifest.CountrySha256) })
        {
            if (string.IsNullOrWhiteSpace(expected) || !File.Exists(file)) throw new UserError("内置主内核或规则库不完整，请修复安装。");
            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new UserError("内置主内核或规则库校验失败，请修复安装。");
        }
        Version = manifest.Version;
    }

    private HelperInstance? ReadIdentity()
    {
        if (!File.Exists(IdentityFile)) return null;
        try { return JsonSerializer.Deserialize<HelperInstance>(File.ReadAllText(IdentityFile)) ?? throw new UserError("主内核进程记录损坏，请检查诊断。"); }
        catch (JsonException) { throw new UserError("主内核进程记录损坏，请检查诊断。"); }
    }

    private bool IsAlive(HelperInstance identity)
    {
        if (!string.Equals(identity.Path, paths.MainCoreExe, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == identity.StartTime && string.Equals(process.MainModule?.FileName, paths.MainCoreExe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { throw new UserError("无法核验主内核进程身份，本次未停止或接管它。"); }
    }

    public bool OwnListenerReady(int port)
    {
        var identity = ReadIdentity();
        return identity is not null && IsAlive(identity) && HelperManager.TcpOwners(port).Contains(identity.Pid);
    }

    internal bool HasOwnProcess => ReadIdentity() is { } identity && IsAlive(identity);

    public int[] OwnedListenerPorts()
    {
        var identity = ReadIdentity();
        return identity is not null && IsAlive(identity)
            ? IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).Distinct().Where(port => HelperManager.TcpOwners(port).Contains(identity.Pid)).ToArray()
            : [];
    }

    // PrepareStandalone must have written MainConfig first. True means this call launched
    // the core; false means the recorded live process passed identity, listener and API checks.
    public async Task<bool> StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await VerifyAsync(cancellationToken);
        ValidatePrepared(settings);
        var previous = ReadIdentity();
        if (previous is not null && IsAlive(previous))
        {
            if (!OwnListenerReady(settings.MixedPort)) throw new UserError("本助手主内核运行状态不一致，请先停止本助手主内核后重试。");
            await WaitReadyAsync(settings, cancellationToken);
            return false;
        }
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == settings.MixedPort)) throw new UserError($"端口 {settings.MixedPort} 被其他程序占用，本助手未结束该进程。");
        Directory.CreateDirectory(paths.MainRuntimeDirectory);
        RestoreGeoDatabases();
        var beforeLaunch = GeoDatabases.ToDictionary(file => file,
            file => File.GetLastWriteTimeUtc(Path.Combine(paths.MainRuntimeDirectory, file)));
        try { await LaunchAsync(settings, cancellationToken); }
        catch (UserError) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed boot can leave a partial file from Mihomo's attempt to repair a corrupt cache.
            if (!RestoreGeoDatabases(beforeLaunch)) throw;
            await LaunchAsync(settings, cancellationToken);
        }
        return true;
    }

    private bool RestoreGeoDatabases(IReadOnlyDictionary<string, DateTime>? beforeLaunch = null)
    {
        var seeded = false;
        foreach (var file in GeoDatabases)
        {
            var destination = new FileInfo(Path.Combine(paths.MainRuntimeDirectory, file));
            if (destination.Exists && destination.Length > 0
                && (beforeLaunch is null || destination.LastWriteTimeUtc == beforeLaunch[file])) continue;
            var staging = destination.FullName + ".seed";
            File.Copy(Path.Combine(paths.MainCoreDirectory, file), staging, overwrite: true);
            File.Move(staging, destination.FullName, overwrite: true);
            seeded = true;
        }
        if (seeded)
        {
            File.Delete(GeoUpdateReceipt);
            _nextGeoUpdateUtc = null;
        }
        return seeded;
    }

    // Called by the existing maintenance loop after the owned core and VPN are ready.
    internal Task CheckGeoUpdateAsync()
    {
        if (!_geoUpdate.IsCompleted) return _geoUpdate;
        var now = DateTime.UtcNow;
        if (_nextGeoUpdateUtc is null)
        {
            var lastCheck = File.GetLastWriteTimeUtc(GeoUpdateReceipt);
            _nextGeoUpdateUtc = lastCheck <= now ? lastCheck + GeoUpdateInterval : now;
        }
        if (now < _nextGeoUpdateUtc) return Task.CompletedTask;
        _nextGeoUpdateUtc = now + GeoRetryInterval;
        return _geoUpdate = UpdateGeoAsync();
    }

    private async Task UpdateGeoAsync()
    {
        try
        {
            await client.UpdateGeoDatabasesAsync(CancellationToken.None);
            SettingsStore.AtomicWrite(GeoUpdateReceipt, DateTime.UtcNow.ToString("O"));
            _nextGeoUpdateUtc = DateTime.UtcNow + GeoUpdateInterval;
            _geoUpdateFailed = false;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _nextGeoUpdateUtc = DateTime.UtcNow + GeoRetryInterval;
            _geoUpdateFailed = true;
        }
    }

    internal CheckItem GeoUpdateStatus()
    {
        if (!_geoUpdate.IsCompleted) return new("GeoSite / GeoIP", "正在检查更新", "muted");
        if (_geoUpdateFailed) return new("GeoSite / GeoIP", "本轮更新未完成，将在 30 分钟后重试", "attention");
        return File.Exists(GeoUpdateReceipt)
            ? new("GeoSite / GeoIP", $"上次成功检查 {File.GetLastWriteTime(GeoUpdateReceipt):yyyy-MM-dd HH:mm}，每 24 小时检查一次", "good")
            : new("GeoSite / GeoIP", "启用分流后自动检查更新", "muted");
    }

    private async Task LaunchAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(paths.MainCoreExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.MainCoreDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-d"); start.ArgumentList.Add(paths.MainRuntimeDirectory); start.ArgumentList.Add("-f"); start.ArgumentList.Add(paths.MainConfig);
        var process = Process.Start(start) ?? throw new UserError("无法启动内置主内核。");
        var identity = new HelperInstance(process.Id, paths.MainCoreExe, process.StartTime.ToUniversalTime());
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            SettingsStore.AtomicWrite(IdentityFile, JsonSerializer.Serialize(identity));
            for (var attempt = 0; attempt < 50; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new UserError("内置主内核启动失败，请检查配置、端口或安装文件。");
                if (OwnListenerReady(settings.MixedPort))
                {
                    await WaitReadyAsync(settings, cancellationToken);
                    _launchedProcess?.Dispose(); _launchedProcess = process;
                    return;
                }
                await Task.Delay(100, cancellationToken);
            }
            throw new UserError("内置主内核未就绪，已停止本次启动的进程。");
        }
        catch
        {
            HelperManager.StopIdentity(identity);
            process.Dispose();
            if (ReadIdentity() == identity) File.Delete(IdentityFile);
            throw;
        }
    }

    private void ValidatePrepared(AppSettings settings)
    {
        ProfileService.ValidateStandalone(settings);
        if (!File.Exists(paths.MainConfig)) throw new UserError("请先生成独立主内核配置。");
        var actual = ProfileService.Parse(File.ReadAllText(paths.MainConfig));
        if (ProfileService.Node(actual, "rules") is not YamlSequenceNode rules || rules.Children.Any(rule => rule is not YamlScalarNode)) throw new UserError("独立主内核规则无效，请重新保存配置。");
        var expected = ProfileService.Parse(ProfileService.GenerateStandalone(settings, paths.MainPipeName, rules.Children.Cast<YamlScalarNode>().Select(rule => rule.Value ?? "").ToArray()));
        if (ProfileService.Dump(actual) != ProfileService.Dump(expected)) throw new UserError("独立主内核配置与当前设置不一致，请重新保存配置。");
    }

    private async Task CheckReadyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await HelperManager.SocksGreetingAsync(settings.MixedPort, cancellationToken);
        var status = await client.StatusAsync(cancellationToken);
        if (status.Port != settings.MixedPort || !status.RuleMode || !status.Ipv6Off || !status.TunOff || !await client.StandaloneRoutingMatchesAsync(settings.Provider, cancellationToken)) throw new UserError("主内核配置回读不一致，本次未接管系统代理。");
        Version = status.Version;
    }

    private async Task WaitReadyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    if (OwnListenerReady(settings.MixedPort)) { await CheckReadyAsync(settings, deadline.Token); return; }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or UserError or OperationCanceledException) { }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new UserError("内置主内核接口或路由未能及时就绪，请检查配置和安装文件。"); }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var settings = await ReloadPreparedConfigAsync(cancellationToken);
        await WaitReadyAsync(settings, cancellationToken);
    }

    internal async Task<AppSettings> ReloadPreparedConfigAsync(CancellationToken cancellationToken)
    {
        await VerifyAsync(cancellationToken);
        var settings = ProfileService.ReadStandaloneSettings(paths.MainConfig);
        ValidatePrepared(settings);
        if (!OwnListenerReady(settings.MixedPort)) throw new UserError("本助手主内核未运行，未向其他内核发送重载请求。");
        await client.ReloadAsync(paths.MainConfig, cancellationToken);
        return settings;
    }

    internal async Task VerifyCustomRulesAsync(AppSettings settings, IReadOnlyList<CustomRule> rules, CancellationToken cancellationToken)
    {
        await WaitReadyAsync(settings, cancellationToken);
        var expectedCount = ProfileService.ReadStandaloneRules(File.ReadAllText(paths.MainConfig)).Count;
        if (!await client.CustomRulesMatchAsync(settings.Provider, rules, expectedCount, cancellationToken))
            throw new UserError("主内核的自定义规则回读与配置不一致。");
    }

    public void StopOwn()
    {
        var identity = ReadIdentity();
        if (identity is not null && IsAlive(identity)) HelperManager.StopIdentity(identity);
        if (identity is not null && !IsAlive(identity)) File.Delete(IdentityFile);
        _launchedProcess?.Dispose(); _launchedProcess = null;
    }

    public void Dispose() => _launchedProcess?.Dispose();
}
