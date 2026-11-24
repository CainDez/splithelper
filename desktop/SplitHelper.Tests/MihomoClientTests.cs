using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class MihomoClientTests
{
    [Fact]
    public async Task Status_and_routing_are_read_back_over_unique_named_pipe_without_controller_secret()
    {
        var name = "SplitHelper.Tests-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(name,
        [
            new("/configs", """{"mixed-port":23456,"mode":"rule","ipv6":false,"tun":{"enable":false}}"""),
            new("/version", """{"version":"test-core-v1"}"""),
            new("/proxies", """{"proxies":{"Windscribe":{"now":"Official"},"ChinaDirect":{}}}"""),
            new("/rules", """{"rules":[{"type":"ProcessName","payload":"Windscribe.exe","proxy":"DIRECT"}]}""")
        ], timeout.Token);
        using var client = new MihomoClient(name);
        try
        {
            Assert.Equal(new CoreStatus(23456, true, true, true, "test-core-v1"), await client.StatusAsync(timeout.Token));
            Assert.True(await client.RoutingMatchesAsync("Windscribe", timeout.Token));
            await server;
        }
        finally { await timeout.CancelAsync(); await IgnoreCancellationAsync(server); }
    }

    [Theory]
    [InlineData("{\"proxies\":{\"Windscribe\":{\"now\":\"Other\"},\"ChinaDirect\":{}}}")]
    [InlineData("{\"proxies\":{\"Windscribe\":{\"now\":\"Official\"}}}")]
    public async Task Routing_rejects_wrong_selection_or_missing_domestic_proxy(string response)
    {
        var name = "SplitHelper.Tests-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(name, [new("/proxies", response)], timeout.Token);
        using var client = new MihomoClient(name);
        try { Assert.False(await client.RoutingMatchesAsync("Windscribe", timeout.Token)); await server; }
        finally { await timeout.CancelAsync(); await IgnoreCancellationAsync(server); }
    }

    [Theory]
    [InlineData("{\"rules\":[]}")]
    [InlineData("{\"rules\":[{\"type\":\"ProcessName\",\"payload\":\"Other.exe\",\"proxy\":\"DIRECT\"}]}")]
    [InlineData("{\"rules\":[{\"type\":\"ProcessName\",\"payload\":\"Windscribe.exe\",\"proxy\":\"Windscribe\"}]}")]
    public async Task Routing_rejects_missing_or_incorrect_first_process_exclusion(string rules)
    {
        var name = "SplitHelper.Tests-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(name,
        [
            new("/proxies", """{"proxies":{"Windscribe":{"now":"Official"},"ChinaDirect":{}}}"""),
            new("/rules", rules)
        ], timeout.Token);
        using var client = new MihomoClient(name);
        try { Assert.False(await client.RoutingMatchesAsync("Windscribe", timeout.Token)); await server; }
        finally { await timeout.CancelAsync(); await IgnoreCancellationAsync(server); }
    }

    [Fact]
    public async Task Status_propagates_controller_http_failure()
    {
        var name = "SplitHelper.Tests-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(name, [new("/configs", "{}", 503)], timeout.Token);
        using var client = new MihomoClient(name);
        try
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.StatusAsync(timeout.Token));
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, error.StatusCode);
            await server;
        }
        finally { await timeout.CancelAsync(); await IgnoreCancellationAsync(server); }
    }

    [Fact]
    public async Task Custom_rule_readback_checks_order_policy_enabled_state_and_full_deletion()
    {
        const string valid = """{"rules":[{"type":"Domain","payload":"first.example","proxy":"REJECT"},{"type":"DomainSuffix","payload":"second.example","proxy":"VPNDirect"},{"type":"Match","proxy":"Windscribe"}]}""";
        const string swapped = """{"rules":[{"type":"DomainSuffix","payload":"second.example","proxy":"VPNDirect"},{"type":"Domain","payload":"first.example","proxy":"REJECT"},{"type":"Match","proxy":"Windscribe"}]}""";
        var wrongPolicy = valid.Replace("VPNDirect", "ChinaDirect");
        var disabled = valid.Replace("\"type\":\"Domain\"", "\"type\":\"Domain\",\"extra\":{\"disabled\":true}");
        const string baseOnly = """{"rules":[{"type":"Match","proxy":"Windscribe"}]}""";
        var name = "SplitHelper.Tests-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(name,
            [new("/rules", valid), new("/rules", swapped), new("/rules", wrongPolicy), new("/rules", disabled), new("/rules", valid), new("/rules", baseOnly)],
            timeout.Token);
        using var client = new MihomoClient(name);
        var rules = new CustomRule[] { new("first.example", false, "REJECT"), new("disabled.example", true, "REJECT", false), new("second.example") };
        try
        {
            Assert.True(await client.CustomRulesMatchAsync("Windscribe", rules, 3, timeout.Token));
            Assert.False(await client.CustomRulesMatchAsync("Windscribe", rules, 3, timeout.Token));
            Assert.False(await client.CustomRulesMatchAsync("Windscribe", rules, 3, timeout.Token));
            Assert.False(await client.CustomRulesMatchAsync("Windscribe", rules, 3, timeout.Token));
            Assert.False(await client.CustomRulesMatchAsync("Windscribe", [], 1, timeout.Token));
            Assert.True(await client.CustomRulesMatchAsync("Windscribe", [], 1, timeout.Token));
            await server;
        }
        finally { await timeout.CancelAsync(); await IgnoreCancellationAsync(server); }
    }

    [Fact]
    public async Task Covered_site_is_checked_through_the_requested_outbound()
    {
        var name = "SplitHelper.Tests-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(name,
        [
            new("/proxies/ChinaDirect/delay?timeout=7000&url=https%3A%2F%2Fwww.baidu.com%2F&expected=200-399", """{"delay":125}"""),
            new("/proxies/VPNDirect/delay?timeout=7000&url=https%3A%2F%2Fwww.cloudflare.com%2Fcdn-cgi%2Ftrace&expected=200-399", "{}", 503)
        ], timeout.Token);
        using var client = new MihomoClient(name);
        try
        {
            await client.ProbeProxyAsync("ChinaDirect", "https://www.baidu.com/", timeout.Token);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ProbeProxyAsync("VPNDirect", "https://www.cloudflare.com/cdn-cgi/trace", timeout.Token));
            await server;
        }
        finally { await timeout.CancelAsync(); await IgnoreCancellationAsync(server); }
    }

    internal sealed record Reply(string Path, string Body, int Status = 200, string Method = "GET");

    internal static async Task ServeAsync(string name, Reply[] replies, CancellationToken cancellationToken)
    {
        foreach (var reply in replies)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(pipe, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            Assert.Equal($"{reply.Method} {reply.Path} HTTP/1.1", await reader.ReadLineAsync(cancellationToken));
            for (var count = 0; ; count++)
            {
                Assert.True(count < 100, "Request header exceeded fixture limit.");
                var line = await reader.ReadLineAsync(cancellationToken);
                Assert.NotNull(line);
                if (line.Length == 0) break;
                Assert.False(line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase), "Pipe requests should not require a TCP controller secret.");
            }
            var bytes = Encoding.UTF8.GetBytes(reply.Body);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} {(reply.Status == 200 ? "OK" : "Service Unavailable")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await pipe.WriteAsync(header, cancellationToken);
            await pipe.WriteAsync(bytes, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
    }

    private static async Task IgnoreCancellationAsync(Task server)
    {
        try { await server; }
        catch (OperationCanceledException) { }
    }
}
