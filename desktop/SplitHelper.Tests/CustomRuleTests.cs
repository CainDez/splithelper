using System.Text;
using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class CustomRuleTests
{
    [Theory]
    [InlineData(" Example.COM. ", "example.com")]
    [InlineData("HTTPS://BÜCHER.de:8443/articles?q=1#start", "xn--bcher-kva.de")]
    [InlineData("https://xn--bcher-kva.de/", "xn--bcher-kva.de")]
    [InlineData("https://example.com/report,2026?q=*", "example.com")]
    [InlineData("https://example.com/search?q=DOMAIN,example.org,REJECT", "example.com")]
    public void Domains_and_http_urls_are_normalized(string input, string expected)
    {
        var rule = new CustomRule(input, false, "REJECT", false);
        Assert.Equal(rule with { Domain = expected }, Assert.Single(ProfileService.NormalizeCustomRules([rule])));
    }

    [Theory]
    [InlineData("com")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("https://[::1]/")]
    [InlineData("*.example.com")]
    [InlineData("example.com,REJECT")]
    [InlineData("example.com\n")]
    [InlineData("bad-.example")]
    [InlineData("bad_label.example")]
    [InlineData("a..example")]
    [InlineData("example.com/path")]
    [InlineData("ftp://example.com/")]
    public void Invalid_domains_are_rejected(string input)
        => Assert.Throws<UserError>(() => ProfileService.NormalizeCustomRules([new(input)]));

    [Fact]
    public void Duplicates_are_rejected_even_when_disabled_and_different_scopes_are_kept()
    {
        var exact = new CustomRule("example.com", false, "REJECT");
        var suffix = new CustomRule("https://EXAMPLE.COM/", true);
        Assert.Equal(new[] { exact, suffix with { Domain = "example.com" } }, ProfileService.NormalizeCustomRules([exact, suffix]));
        Assert.Throws<UserError>(() => ProfileService.NormalizeCustomRules([exact, suffix with { IncludeSubdomains = false, Enabled = false }]));
        Assert.Throws<UserError>(() => ProfileService.NormalizeCustomRules([exact with { Target = "DIRECT" }]));
        Assert.Throws<UserError>(() => ProfileService.NormalizeCustomRules([null!]));
    }

    [Theory]
    [InlineData("Windscribe", "VPNDirect")]
    [InlineData("Mullvad", "Official")]
    public void Custom_rules_precede_base_rules_and_map_to_the_selected_provider(string provider, string vpn)
    {
        var settings = new AppSettings { Provider = provider };
        var baseRules = new[] { "DOMAIN,blocked.example,ChinaDirect", $"MATCH,{provider}" };
        var rules = new CustomRule[]
        {
            new("blocked.example", false, "REJECT"),
            new("example.net", true, "VPN"),
            new("example.org", true, "ChinaDirect"),
            new("disabled.example", false, "REJECT", false)
        };
        var config = ProfileService.GenerateStandalone(settings, "private", baseRules, rules);
        Assert.Equal(new[]
        {
            "DOMAIN,blocked.example,REJECT", $"DOMAIN-SUFFIX,example.net,{vpn}",
            "DOMAIN-SUFFIX,example.org,ChinaDirect", baseRules[0], baseRules[1]
        }, ProfileService.ReadStandaloneRules(config));
    }

    [Fact]
    public void Repeated_prepare_rebind_and_delete_preserve_migrated_base_and_ignore_pending()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        var clash = temp.Child("clash");
        Directory.CreateDirectory(clash);
        var profiles = new ProfileService(paths, new ClientEnvironment(clashHome: clash));
        Assert.Empty(profiles.LoadCustomRules());
        var metadata = ProfileService.MergeMetadata("items: []\n", "Windscribe");
        File.WriteAllText(Path.Combine(clash, "profiles.yaml"), metadata);
        const string legacy = "rules:\n- DOMAIN,base.example,Official\n- MATCH,Windscribe\n";
        var legacyPath = Path.Combine(clash, "clash-verge.yaml");
        File.WriteAllText(legacyPath, legacy);
        var settings = new AppSettings { WindscribeInterface = "Windscribe IKEv2" };
        SettingsStore.AtomicWrite(paths.CustomRulesFile, ProfileService.SerializeCustomRules([new("keep.example", false, "REJECT")]));
        var snapshot = Path.Combine(paths.MainRuntimeDirectory, "rules-Windscribe.yaml");
        Assert.Equal("DOMAIN,keep.example,REJECT", ProfileService.ReadStandaloneRules(profiles.RenderStandalone(settings))[0]);
        Assert.False(File.Exists(snapshot));
        Assert.False(File.Exists(paths.MainConfig));

        profiles.PrepareStandalone(settings);
        var baseRules = new[] { "DOMAIN,base.example,VPNDirect", "MATCH,Windscribe" };
        Assert.Equal(baseRules, ProfileService.ReadStandaloneRules(File.ReadAllText(snapshot)));
        var snapshotYaml = "# migrated rule comment\r\n" + File.ReadAllText(snapshot);
        File.WriteAllText(snapshot, snapshotYaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var originalSnapshot = File.ReadAllBytes(snapshot);
        SettingsStore.AtomicWrite(paths.PendingCustomRulesFile, ProfileService.SerializeCustomRules([new("pending.example")]));

        settings.WindscribeInterface = "WindscribeWireguard";
        profiles.PrepareStandalone(settings);
        Assert.Equal("WindscribeWireguard", ProfileService.ReadStandaloneSettings(paths.MainConfig).WindscribeInterface);
        Assert.Equal(new[] { "DOMAIN,keep.example,REJECT", baseRules[0], baseRules[1] }, ProfileService.ReadStandaloneRules(File.ReadAllText(paths.MainConfig)));
        Assert.Equal(originalSnapshot, File.ReadAllBytes(snapshot));
        Assert.Equal(legacy, File.ReadAllText(legacyPath));

        SettingsStore.AtomicWrite(paths.CustomRulesFile, "[]");
        Assert.Equal(baseRules, ProfileService.ReadStandaloneRules(profiles.RenderStandalone(settings)));
        profiles.PrepareStandalone(settings);
        Assert.Equal(baseRules, ProfileService.ReadStandaloneRules(File.ReadAllText(paths.MainConfig)));
        Assert.Equal(originalSnapshot, File.ReadAllBytes(snapshot));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task Invalid_committed_file_is_never_overwritten(string contents)
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        var environment = new ClientEnvironment(clashHome: temp.Child("clash"));
        var profiles = new ProfileService(paths, environment);
        using var connection = Connection(paths, environment, profiles);
        SettingsStore.AtomicWrite(paths.CustomRulesFile, contents);

        await Assert.ThrowsAsync<UserError>(() => connection.SaveCustomRulesAsync([new("new.example")], default));
        Assert.Equal(contents, File.ReadAllText(paths.CustomRulesFile));
        Assert.False(File.Exists(paths.PendingCustomRulesFile));
    }

    [Fact]
    public async Task Inactive_save_commits_only_json_and_cancelled_save_leaves_previous_values()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        var environment = new ClientEnvironment(clashHome: temp.Child("clash"));
        var profiles = new ProfileService(paths, environment);
        using var connection = Connection(paths, environment, profiles);
        Assert.False(await connection.SaveCustomRulesAsync([new("EXAMPLE.com")], default));
        Assert.Equal(new CustomRule("example.com"), Assert.Single(profiles.LoadCustomRules()));
        Assert.False(Directory.Exists(paths.MainRuntimeDirectory));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SaveCustomRulesAsync([], new CancellationToken(canceled: true)));
        Assert.Equal(new CustomRule("example.com"), Assert.Single(profiles.LoadCustomRules()));
        Assert.False(File.Exists(paths.PendingCustomRulesFile));
    }

    [Theory]
    [InlineData("Mullvad", "Official")]
    [InlineData("Windscribe", "VPNDirect")]
    public async Task Maintenance_rebuilds_committed_rules_before_core_start_and_keeps_marker_on_failed_recovery(string provider, string vpn)
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        var environment = new ClientEnvironment(clashHome: temp.Child("clash"));
        var profiles = new ProfileService(paths, environment);
        using var connection = Connection(paths, environment, profiles);
        var runtime = new AppSettings { Provider = provider, MixedPort = 17898 };
        SettingsStore.AtomicWrite(paths.CustomRulesFile, ProfileService.SerializeCustomRules([new("committed.example")]));
        SettingsStore.AtomicWrite(paths.PendingCustomRulesFile, ProfileService.SerializeCustomRules([new("candidate.example")]));
        SettingsStore.AtomicWrite(paths.MainConfig, ProfileService.GenerateStandalone(runtime, paths.MainPipeName, customRules: [new("candidate.example")]));
        var original = new ProxySnapshot(1, "", "", "");
        SettingsStore.WriteProtected(paths.ProxySessionFile, new ProxySession(original, original with { Flags = 3, Server = "127.0.0.1:17898" }, 17898));
        var sessionBytes = File.ReadAllBytes(paths.ProxySessionFile);

        // The isolated test install has no main-core manifest, so recovery stops before any network calls.
        await Assert.ThrowsAsync<UserError>(() => connection.MaintainAsync(new AppSettings { Provider = provider == "Mullvad" ? "Windscribe" : "Mullvad" }, default));
        Assert.Equal($"DOMAIN-SUFFIX,committed.example,{vpn}", ProfileService.ReadStandaloneRules(File.ReadAllText(paths.MainConfig))[0]);
        Assert.Equal(new CustomRule("committed.example"), Assert.Single(profiles.LoadCustomRules()));
        Assert.True(File.Exists(paths.PendingCustomRulesFile));
        Assert.Equal(sessionBytes, File.ReadAllBytes(paths.ProxySessionFile));
        var error = await Assert.ThrowsAsync<UserError>(() => connection.SaveCustomRulesAsync([new("another.example")], default));
        Assert.Contains("恢复待处理", error.Message);
    }

    [Fact]
    public void Probe_selection_uses_first_enabled_exact_or_suffix_match_and_default_policy()
    {
        var defaults = ("https://www.cloudflare.com/cdn-cgi/trace", false);
        Assert.Equal(defaults, ConnectionService.SelectProbeRoutes([new("cloudflare.com", false, "REJECT"), new("baidu.com", true, "REJECT", false)]));
        Assert.Equal(defaults, ConnectionService.SelectProbeRoutes([new("www.cloudflare.com", false), new("cloudflare.com", true, "REJECT"), new("baidu.com", true, "ChinaDirect")]));
        Assert.Equal(("https://1.1.1.1/cdn-cgi/trace", true),
            ConnectionService.SelectProbeRoutes([new("cloudflare.com", true, "REJECT"), new("www.cloudflare.com", false), new("www.baidu.com", false)]));
    }

    private static ConnectionService Connection(AppPaths paths, ClientEnvironment environment, ProfileService profiles)
        => new(paths, environment, new SettingsStore(paths), profiles, new HelperManager(paths), new WindowsProxy(paths), new StartupService(paths), new MihomoClient(paths.MainPipeName));
}
