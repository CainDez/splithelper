using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class ProxySnapshotTests
{
    [Fact]
    public void SameAs_detects_external_changes_to_port_flags_pac_and_bypass()
    {
        var original = new ProxySnapshot(3, "127.0.0.1:23456", "<local>;*.internal", "");
        Assert.True(original.SameAs(original with { }));
        Assert.False(original.SameAs(original with { Server = "127.0.0.1:23457" }));
        Assert.False(original.SameAs(original with { Flags = 7 }));
        Assert.False(original.SameAs(original with { AutoConfigUrl = "http://localhost/proxy.pac" }));
        Assert.False(original.SameAs(original with { Bypass = "<local>" }));
    }

    [Theory]
    [InlineData(3u, "127.0.0.1:23456", true)]
    [InlineData(1u, "127.0.0.1:23456", false)]
    [InlineData(3u, "127.0.0.1:23457", false)]
    [InlineData(3u, "127.0.0.1:234560", false)]
    [InlineData(3u, "localhost:23456", true)]
    [InlineData(3u, "http=127.0.0.1:23456;https=127.0.0.1:23456", true)]
    [InlineData(3u, "[::1]:23456", true)]
    [InlineData(3u, "http=example.org:8080;https=127.0.0.1:23456", true)]
    [InlineData(3u, "example.org:23456", false)]
    [InlineData(3u, "http://LOCALHOST:23456", true)]
    public void PointsTo_detects_enabled_loopback_dependencies_in_all_protocol_entries(uint flags, string server, bool expected) =>
        Assert.Equal(expected, new ProxySnapshot(flags, server, "", "").PointsTo(23456));
}
