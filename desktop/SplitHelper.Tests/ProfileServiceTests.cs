using SplitHelper.Services;
using YamlDotNet.RepresentationModel;

namespace SplitHelper.Tests;

public sealed class ProfileServiceTests
{
    private static AppSettings Settings(string provider = "Windscribe") => new()
    {
        Provider = provider, MixedPort = 23456, GatewayHost = "10.23.45.1", GatewayPort = 32123,
        GatewayUsername = "用户\nname: \"quoted\" # literal", GatewayPassword = "密码\npass: 'quoted' # literal\\end"
    };

    [Theory]
    [InlineData("Mullvad")]
    [InlineData("Windscribe")]
    public void Generate_preserves_full_rules_dns_and_all_other_template_fields(string provider)
    {
        var original = ProfileService.Parse(ProfileService.Resource($"Templates.{provider}.yaml"));
        var generated = ProfileService.Parse(ProfileService.Generate(Settings(provider)));
        Assert.Equal("23456", ProfileService.Value(generated, "mixed-port"));
        foreach (var entry in original.Children)
        {
            var key = ((YamlScalarNode)entry.Key).Value!;
            if (key is "mixed-port" or "proxies") continue;
            AssertYamlEqual(entry.Value, ProfileService.Node(generated, key)!);
        }
        var oldProxies = Assert.IsType<YamlSequenceNode>(ProfileService.Node(original, "proxies"));
        var newProxies = Assert.IsType<YamlSequenceNode>(ProfileService.Node(generated, "proxies"));
        Assert.Equal(oldProxies.Children.Count, newProxies.Children.Count);
        var china = Proxy(generated, "ChinaDirect");
        AssertYamlEqual(Proxy(original, "ChinaDirect"), china);
        Assert.Equal("127.0.0.1", ProfileService.Value(china, "server"));
        Assert.Equal("18997", ProfileService.Value(china, "port"));
        if (provider == "Mullvad")
            AssertYamlEqual(Proxy(original, "Official"), Proxy(generated, "Official"));
        else
        {
            Assert.Equal("strict", ProfileService.Value(generated, "find-process-mode"));
            Assert.Null(ProfileService.Node(generated, "interface-name"));
            var rules = Assert.IsType<YamlSequenceNode>(ProfileService.Node(generated, "rules"));
            Assert.Equal("PROCESS-NAME,Windscribe.exe,DIRECT", Assert.IsType<YamlScalarNode>(rules.Children[0]).Value);
        }
    }

    [Fact]
    public void Generate_round_trips_special_credentials_and_redacts_both_fields()
    {
        var settings = Settings();
        var official = Proxy(ProfileService.Parse(ProfileService.Generate(settings)), "Official");
        Assert.Equal(settings.GatewayUsername, ProfileService.Value(official, "username"));
        Assert.Equal(settings.GatewayPassword, ProfileService.Value(official, "password"));
        Assert.Equal(settings.GatewayHost, ProfileService.Value(official, "server"));
        Assert.Equal(settings.GatewayPort.ToString(), ProfileService.Value(official, "port"));
        var redacted = ProfileService.Generate(settings, redact: true);
        var safeProxy = Proxy(ProfileService.Parse(redacted), "Official");
        Assert.Equal("[已隐藏]", ProfileService.Value(safeProxy, "username"));
        Assert.Equal("[已隐藏]", ProfileService.Value(safeProxy, "password"));
        Assert.DoesNotContain("用户", redacted);
        Assert.DoesNotContain("密码", redacted);
    }

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.23")]
    [InlineData("203.0.113.254")]
    [InlineData("10.1.1.256")]
    [InlineData("010.1.1.1")]
    [InlineData("gateway.example")]
    public void Generate_rejects_documentation_or_noncanonical_gateway_addresses(string host)
    {
        var settings = Settings(); settings.GatewayHost = host;
        Assert.Throws<UserError>(() => ProfileService.Generate(settings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(18997)]
    public void Generate_rejects_invalid_or_helper_conflicting_mixed_port(int port)
    {
        var settings = Settings(); settings.MixedPort = port;
        Assert.Throws<UserError>(() => ProfileService.Generate(settings));
    }

    [Theory]
    [InlineData(0, "user", "password")]
    [InlineData(65536, "user", "password")]
    [InlineData(1080, "", "password")]
    [InlineData(1080, " ", "password")]
    [InlineData(1080, "user", "")]
    public void Generate_rejects_invalid_gateway_port_or_empty_credentials(int port, string user, string password)
    {
        var settings = Settings(); settings.GatewayPort = port; settings.GatewayUsername = user; settings.GatewayPassword = password;
        Assert.Throws<UserError>(() => ProfileService.Generate(settings));
    }

    [Theory]
    [InlineData("Mullvad")]
    [InlineData("Windscribe")]
    public void Bundled_dns_script_matches_repository_source(string provider) =>
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", provider + ".js")), ProfileService.Resource($"Templates.{provider}.js"));

    private static YamlMappingNode Proxy(YamlMappingNode root, string name) =>
        Assert.IsType<YamlSequenceNode>(ProfileService.Node(root, "proxies")).Children.OfType<YamlMappingNode>().Single(item => ProfileService.Value(item, "name") == name);

    internal static void AssertYamlEqual(YamlNode expected, YamlNode actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.NodeType, actual.NodeType);
        if (expected is YamlScalarNode scalar) Assert.Equal(scalar.Value, Assert.IsType<YamlScalarNode>(actual).Value);
        else if (expected is YamlSequenceNode sequence)
        {
            var other = Assert.IsType<YamlSequenceNode>(actual);
            Assert.Equal(sequence.Children.Count, other.Children.Count);
            for (var i = 0; i < sequence.Children.Count; i++) AssertYamlEqual(sequence.Children[i], other.Children[i]);
        }
        else if (expected is YamlMappingNode map)
        {
            var other = Assert.IsType<YamlMappingNode>(actual);
            Assert.Equal(map.Children.Count, other.Children.Count);
            foreach (var pair in map.Children)
            {
                Assert.True(other.Children.TryGetValue(pair.Key, out var value), $"Missing YAML key: {pair.Key}");
                AssertYamlEqual(pair.Value, value!);
            }
        }
    }
}
