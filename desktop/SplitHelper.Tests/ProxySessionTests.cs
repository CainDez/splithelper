using System.Text.Json;
using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class ProxySessionTests
{
    [Fact]
    public void Legacy_journal_only_represents_default_connection()
    {
        var session = JsonSerializer.Deserialize<ProxySession>("""{"Original":{"Flags":1,"Server":"","Bypass":"<local>","AutoConfigUrl":""},"Applied":{"Flags":3,"Server":"127.0.0.1:7997","Bypass":"<local>","AutoConfigUrl":""},"Port":7997}""")!;
        var entry = Assert.Single(session.Entries);
        Assert.Null(entry.Name);
        Assert.Equal(1u, entry.Original.Flags);
        Assert.Equal(7997, session.Port);
        Assert.True(entry.Applied.PointsTo(7997));
    }
    [Fact]
    public void Per_connection_journal_roundtrip_retains_distinct_originals()
    {
        var original = new ProxySnapshot(1, "", "<local>", "");
        var rasOriginal = new ProxySnapshot(7, "localhost:30001", "*.internal", "https://proxy.example/pac");
        var applied = new ProxySnapshot(3, "127.0.0.1:17897", "<local>", "");
        var session = new ProxySession(original, applied, 17897, [new(null, original, applied), new("Windscribe IKEv2", rasOriginal, applied with { Bypass = rasOriginal.Bypass })]);
        var result = JsonSerializer.Deserialize<ProxySession>(JsonSerializer.Serialize(session))!;
        Assert.Equal(session.Entries, result.Entries);
        Assert.NotEqual(result.Entries[0].Original, result.Entries[1].Original);
    }
}
