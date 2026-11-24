using SplitHelper.Services;
using YamlDotNet.RepresentationModel;

namespace SplitHelper.Tests;

public sealed class StandaloneProfileTests
{
    [Fact]
    public void Migration_preserves_rule_parameters_and_normalizes_types_like_mihomo()
    {
        var map = ProfileService.Parse(ProfileService.GenerateStandalone(new AppSettings(), "private-main", ["ip-cidr,192.0.2.0/24,Official,src,no-resolve", "DOMAIN-REGEX,^(a|b),c$,ChinaDirect", "match,Windscribe"]));
        Assert.Equal(new[] { "IP-CIDR,192.0.2.0/24,VPNDirect,src,no-resolve", "DOMAIN-REGEX,^(a|b),c$,ChinaDirect", "MATCH,Windscribe" }, Rules(map));
    }

    [Fact]
    public void Interrupted_takeover_uses_prepared_route_settings_without_rewriting_preferences()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        var store = new SettingsStore(paths);
        store.Save(new AppSettings { Provider = "Mullvad", MixedPort = 7897 });
        Directory.CreateDirectory(paths.MainRuntimeDirectory);
        File.WriteAllText(paths.MainConfig, ProfileService.GenerateStandalone(new AppSettings { MixedPort = 17898, WindscribeInterface = "WindscribeWireguard" }, paths.MainPipeName));
        var original = new ProxySnapshot(1, "", "", "");
        SettingsStore.WriteProtected(paths.ProxySessionFile, new ProxySession(original, original with { Flags = 3, Server = "127.0.0.1:17898" }, 17898));
        using var model = new DesktopViewModel(paths, true);
        Assert.Equal("17898", model.MixedPort);
        Assert.Equal("Windscribe", model.Provider);
        Assert.Equal(7897, store.Load().MixedPort);
        Assert.Equal("WindscribeWireguard", ProfileService.ReadStandaloneSettings(paths.MainConfig).WindscribeInterface);
    }

    [Theory]
    [InlineData("Windscribe IKEv2")]
    [InlineData("WindscribeWireguard")]
    public void Detected_protocol_changes_binding_without_changing_rules(string vpnInterface)
    {
        var rules = new[] { "DOMAIN,custom.example,ChinaDirect", "MATCH,Windscribe" };
        var map = ProfileService.Parse(ProfileService.GenerateStandalone(new AppSettings { WindscribeInterface = vpnInterface }, "private-main", rules));
        var vpn = Assert.IsType<YamlSequenceNode>(ProfileService.Node(map, "proxies")).Children.OfType<YamlMappingNode>().Single(proxy => ProfileService.Value(proxy, "name") == "VPNDirect");
        Assert.Equal(vpnInterface, ProfileService.Value(vpn, "interface-name"));
        Assert.Equal(rules, Rules(map));
    }

    [Theory]
    [InlineData("WLAN")]
    [InlineData("Ethernet")]
    public void Physical_interface_is_never_accepted_as_the_vpn_exit(string vpnInterface)
        => Assert.Throws<UserError>(() => ProfileService.GenerateStandalone(new AppSettings { WindscribeInterface = vpnInterface }, "private-main"));

    [Fact]
    public void Windscribe_needs_no_gateway_and_binds_outbound_and_dns_to_vpn()
    {
        var map = ProfileService.Parse(ProfileService.GenerateStandalone(new AppSettings(), "private-main"));
        var proxies = Assert.IsType<YamlSequenceNode>(ProfileService.Node(map, "proxies"));
        var vpn = proxies.Children.OfType<YamlMappingNode>().Single(proxy => ProfileService.Value(proxy, "name") == "VPNDirect");
        Assert.Equal("direct", ProfileService.Value(vpn, "type"));
        Assert.Equal("Windscribe IKEv2", ProfileService.Value(vpn, "interface-name"));
        Assert.Equal("ipv4", ProfileService.Value(vpn, "ip-version"));
        Assert.DoesNotContain(proxies.Children.OfType<YamlMappingNode>(), proxy => ProfileService.Value(proxy, "name") == "Official");
        var dns = Assert.IsType<YamlMappingNode>(ProfileService.Node(map, "dns"));
        Assert.Null(ProfileService.Node(dns, "listen"));
        Assert.Contains("https://1.1.1.1/dns-query#VPNDirect", ProfileService.Dump(dns));
        Assert.Contains("https://223.5.5.5/dns-query#ChinaDirect", ProfileService.Dump(dns));
        Assert.Equal("127.0.0.1", ProfileService.Value(map, "bind-address"));
        Assert.Equal("false", ProfileService.Value(map, "allow-lan"));
        Assert.Equal(@"\\.\pipe\private-main", ProfileService.Value(map, "external-controller-pipe"));
        Assert.DoesNotContain("PROCESS-NAME,Windscribe.exe,DIRECT", ProfileService.Dump(map));
        Assert.Equal("false", ProfileService.Value(map, "ipv6"));
        Assert.Equal("false", ProfileService.Value(Assert.IsType<YamlMappingNode>(ProfileService.Node(map, "tun")), "enable"));
    }

    [Fact]
    public void Mullvad_keeps_official_socks_and_migration_keeps_rule_order()
    {
        var map = ProfileService.Parse(ProfileService.GenerateStandalone(new AppSettings { Provider = "Mullvad" }, "private-main", ["DOMAIN,custom.example,ChinaDirect", "MATCH,Official"]));
        var official = Assert.IsType<YamlSequenceNode>(ProfileService.Node(map, "proxies")).Children.OfType<YamlMappingNode>().Single(proxy => ProfileService.Value(proxy, "name") == "Official");
        Assert.Equal("10.64.0.1", ProfileService.Value(official, "server"));
        Assert.Equal("1080", ProfileService.Value(official, "port"));
        Assert.Equal(new[] { "DOMAIN,custom.example,ChinaDirect", "MATCH,Official" }, Rules(map));
    }

    [Fact]
    public void Migration_snapshots_only_confirmed_effective_rules_and_does_not_write_clash()
    {
        using var temp = new TempDirectory();
        var clash = temp.Child("clash"); Directory.CreateDirectory(clash);
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        var profiles = new ProfileService(paths, new ClientEnvironment(clashHome: clash));
        var metadata = ProfileService.MergeMetadata("items: []\n", "Windscribe");
        File.WriteAllText(Path.Combine(clash, "profiles.yaml"), metadata);
        var active = "rules:\n- PROCESS-NAME,Windscribe.exe,DIRECT\n- DOMAIN,custom.example,ChinaDirect\n- DOMAIN,another.example,Official\n- MATCH,Windscribe\n";
        File.WriteAllText(Path.Combine(clash, "clash-verge.yaml"), active);
        profiles.PrepareStandalone(new AppSettings());
        var expected = new[] { "DOMAIN,custom.example,ChinaDirect", "DOMAIN,another.example,VPNDirect", "MATCH,Windscribe" };
        Assert.Equal(expected, Rules(ProfileService.Parse(File.ReadAllText(paths.MainConfig))));
        Assert.Equal(metadata, File.ReadAllText(Path.Combine(clash, "profiles.yaml")));
        Assert.Equal(active, File.ReadAllText(Path.Combine(clash, "clash-verge.yaml")));
        File.WriteAllText(Path.Combine(clash, "clash-verge.yaml"), "rules: [MATCH,UnknownPolicy]\n");
        profiles.PrepareStandalone(new AppSettings());
        Assert.Equal(expected, Rules(ProfileService.Parse(File.ReadAllText(paths.MainConfig))));
    }

    [Fact]
    public void Unmanaged_clash_is_not_imported()
    {
        using var temp = new TempDirectory();
        var clash = temp.Child("clash"); Directory.CreateDirectory(clash);
        File.WriteAllText(Path.Combine(clash, "profiles.yaml"), "current: other\nitems: []\n");
        File.WriteAllText(Path.Combine(clash, "clash-verge.yaml"), "rules: [MATCH,UnknownPolicy]\n");
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        new ProfileService(paths, new ClientEnvironment(clashHome: clash)).PrepareStandalone(new AppSettings());
        var rules = Rules(ProfileService.Parse(File.ReadAllText(paths.MainConfig)));
        Assert.Contains("GEOSITE,cn,ChinaDirect", rules);
        Assert.Equal("MATCH,Windscribe", rules[^1]);
    }

    [Theory]
    [InlineData("DOMAIN,custom.example,MyCustomProxy")]
    [InlineData("RULE-SET,private,ChinaDirect")]
    [InlineData("MATCH,")]
    [InlineData("MATCH,DIRECT")]
    [InlineData("DOMAIN,custom.example,ChinaDirect")]
    [InlineData("MATCH,Windscribe\n- DOMAIN,custom.example,ChinaDirect")]
    [InlineData("MATCH,Windscribe\n- MATCH,Windscribe")]
    public void Unsupported_rules_fail_before_creating_private_config(string rule)
    {
        using var temp = new TempDirectory();
        var clash = temp.Child("clash"); Directory.CreateDirectory(clash);
        File.WriteAllText(Path.Combine(clash, "profiles.yaml"), ProfileService.MergeMetadata("items: []\n", "Windscribe"));
        File.WriteAllText(Path.Combine(clash, "clash-verge.yaml"), "rules:\n- " + rule + "\n");
        var original = File.ReadAllText(Path.Combine(clash, "clash-verge.yaml"));
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        Assert.Throws<UserError>(() => new ProfileService(paths, new ClientEnvironment(clashHome: clash)).PrepareStandalone(new AppSettings()));
        Assert.False(File.Exists(paths.MainConfig));
        Assert.Equal(original, File.ReadAllText(Path.Combine(clash, "clash-verge.yaml")));
    }

    [Fact]
    public void Private_pipe_names_follow_data_directory_identity()
    {
        using var temp = new TempDirectory();
        var first = new AppPaths(temp.Child("app"), temp.Child("data"));
        Assert.Equal(first.MainPipeName, new AppPaths(temp.Child("other-app"), temp.Child("DATA") + Path.DirectorySeparatorChar).MainPipeName);
        Assert.NotEqual(first.MainPipeName, new AppPaths(temp.Child("app"), temp.Child("other-data")).MainPipeName);
    }

    private static string[] Rules(YamlMappingNode map) => Assert.IsType<YamlSequenceNode>(ProfileService.Node(map, "rules")).Children.Cast<YamlScalarNode>().Select(rule => rule.Value!).ToArray();
}
