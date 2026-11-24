using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class ProxyManagementTests
{
    [Theory]
    [InlineData("enable_system_proxy", "系统代理")]
    [InlineData("enable_proxy_guard", "代理守卫")]
    [InlineData("proxy_auto_config", "自动代理（PAC）")]
    public void Enabled_clash_proxy_management_reports_the_setting_and_blocks_startup(string key, string label)
    {
        using var temp = new TempDirectory();
        var home = temp.Child("clash"); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home,"verge.yaml"), key + ": true\n");
        var profiles = new ProfileService(new AppPaths(temp.Child("install"),temp.Child("data")),new ClientEnvironment(clashHome:home));
        Assert.Contains(label,profiles.ProxyManagementIssue());
        Assert.False(profiles.ProxyGuardDisabled());
    }

    [Theory]
    [InlineData("enable_system_proxy: false\nenable_proxy_guard: false\nproxy_auto_config: false\n")]
    [InlineData("unrelated: true\n")]
    [InlineData("enable_system_proxy: null\nenable_proxy_guard: null\n")]
    [InlineData("enable_system_proxy: ~\nenable_proxy_guard:\n")]
    public void Disabled_or_default_optional_settings_do_not_report_a_conflict(string yaml)
    {
        using var temp = new TempDirectory();
        var home = temp.Child("clash"); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home,"verge.yaml"),yaml);
        var profiles = new ProfileService(new AppPaths(temp.Child("install"),temp.Child("data")),new ClientEnvironment(clashHome:home));
        Assert.Null(profiles.ProxyManagementIssue());
        Assert.True(profiles.ProxyGuardDisabled());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[not-a-mapping]")]
    [InlineData("broken: [")]
    [InlineData("enable_system_proxy: unknown\n")]
    [InlineData("enable_system_proxy: {unexpected: true}\n")]
    [InlineData("proxy_auto_config: [true]\n")]
    public void Missing_or_invalid_settings_cannot_report_proxy_management_as_ready(string? yaml)
    {
        using var temp = new TempDirectory();
        var home = temp.Child("clash"); Directory.CreateDirectory(home);
        if(yaml is not null) File.WriteAllText(Path.Combine(home,"verge.yaml"),yaml);
        var profiles = new ProfileService(new AppPaths(temp.Child("install"),temp.Child("data")),new ClientEnvironment(clashHome:home));
        Assert.NotNull(profiles.ProxyManagementIssue());
        Assert.False(profiles.ProxyGuardDisabled());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_refresh_displays_management_check_before_network_detection(bool conflict)
    {
        using var temp = new TempDirectory();
        var home = temp.Child("clash"); Directory.CreateDirectory(home);
        var yaml="enable_system_proxy: " + (conflict?"true":"false") + "\nenable_proxy_guard: false\nproxy_auto_config: false\n";
        var config=Path.Combine(home,"verge.yaml"); File.WriteAllText(config,yaml);
        var paths=new AppPaths(temp.Child("install"),temp.Child("data"));
        var environment=new ClientEnvironment(clashHome:home);
        var profiles=new ProfileService(paths,environment);
        var pipe="SplitHelper.Tests-"+Guid.NewGuid().ToString("N");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection=new ConnectionService(paths,environment,new SettingsStore(paths),profiles,new HelperManager(paths),new WindowsProxy(paths),new StartupService(paths),new MihomoClient(pipe));
        try
        {
            var report=await connection.RefreshAsync(new AppSettings(),false,timeout.Token);
            var check=Assert.Single(report.Checks,item=>item.Title=="代理管理归属");
            Assert.Equal(conflict?"attention":"good",check.Tone);
            if(conflict){Assert.Equal("代理管理需要处理",report.Headline);Assert.Equal("attention",report.Tone);Assert.Contains("自动启用",report.Subtitle);}
            Assert.Equal(yaml,File.ReadAllText(config));
            Assert.False(File.Exists(paths.ProxySessionFile));
        }
        finally {await timeout.CancelAsync();}
    }
}
