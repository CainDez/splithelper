using System.IO.Pipes;
using System.Net.Http.Json;

namespace SplitHelper.Services;

public sealed record CoreStatus(int Port, bool RuleMode, bool Ipv6Off, bool TunOff, string Version);
public sealed class MihomoClient : IDisposable
{
    private static readonly TimeSpan ControllerTimeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _http;
    public MihomoClient(string pipeName = "verge-mihomo")
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(cancellationToken); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://mihomo/"), Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    }
    public async Task<CoreStatus> StatusAsync(CancellationToken cancellationToken)
    {
        using var configs = await GetAsync("configs", cancellationToken);
        using var version = await GetAsync("version", cancellationToken);
        var root = configs.RootElement;
        var port = root.TryGetProperty("mixed-port", out var mixed) ? mixed.GetInt32() : 0;
        var mode = root.TryGetProperty("mode", out var modeValue) && modeValue.GetString() == "rule";
        var ipv6 = root.TryGetProperty("ipv6", out var ipv6Value) && !ipv6Value.GetBoolean();
        var tun = root.TryGetProperty("tun", out var tunValue) && tunValue.TryGetProperty("enable", out var enabled) && !enabled.GetBoolean();
        return new CoreStatus(port, mode, ipv6, tun, version.RootElement.TryGetProperty("version", out var text) ? text.GetString() ?? "" : "");
    }
    public async Task<bool> RoutingMatchesAsync(string provider, CancellationToken cancellationToken)
    {
        using var proxies = await GetAsync("proxies", cancellationToken);
        if (!proxies.RootElement.TryGetProperty("proxies", out var map) || !map.TryGetProperty(provider, out var group) || !group.TryGetProperty("now", out var current) || current.GetString() != "Official" || !map.TryGetProperty("ChinaDirect", out _)) return false;
        if (provider != "Windscribe") return true;
        using var rules = await GetAsync("rules", cancellationToken);
        if (!rules.RootElement.TryGetProperty("rules", out var list) || list.GetArrayLength() == 0) return false;
        var first = list[0];
        return first.TryGetProperty("type", out var type) && type.GetString() == "ProcessName" && first.TryGetProperty("payload", out var payload) && string.Equals(payload.GetString(), "Windscribe.exe", StringComparison.OrdinalIgnoreCase) && first.TryGetProperty("proxy", out var target) && target.GetString() == "DIRECT";
    }
    public async Task ReloadAsync(string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ControllerTimeout);
        using var response = await _http.PutAsJsonAsync("configs?force=false", new { path = Path.GetFullPath(path) }, deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    internal async Task UpdateGeoDatabasesAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Mihomo allows 90 seconds for each parallel download, followed by validation and writes.
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await _http.PostAsync("configs/geo", null, deadline.Token);
        response.EnsureSuccessStatusCode();
    }
    public async Task<bool> StandaloneRoutingMatchesAsync(string provider, CancellationToken cancellationToken)
    {
        if (provider is not ("Mullvad" or "Windscribe")) return false;
        using var proxies = await GetAsync("proxies", cancellationToken);
        var expected = ProfileService.StandaloneVpnProxy(provider);
        if (!proxies.RootElement.TryGetProperty("proxies", out var map) || !map.TryGetProperty(provider, out var group) || !group.TryGetProperty("now", out var current) || current.GetString() != expected || !map.TryGetProperty(expected, out _) || !map.TryGetProperty("ChinaDirect", out _)) return false;
        using var rules = await GetAsync("rules", cancellationToken);
        if (!rules.RootElement.TryGetProperty("rules", out var list) || list.GetArrayLength() == 0) return false;
        return provider != "Windscribe" || !list.EnumerateArray().Any(rule => rule.TryGetProperty("type", out var type) && type.GetString() == "ProcessName" && rule.TryGetProperty("payload", out var payload) && string.Equals(payload.GetString(), "Windscribe.exe", StringComparison.OrdinalIgnoreCase) && rule.TryGetProperty("proxy", out var target) && target.GetString() == "DIRECT");
    }
    internal async Task<bool> CustomRulesMatchAsync(string provider, IReadOnlyList<CustomRule> rules, int expectedCount, CancellationToken cancellationToken)
    {
        using var document = await GetAsync("rules", cancellationToken);
        if (!document.RootElement.TryGetProperty("rules", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != expectedCount) return false;
        var index = 0;
        foreach (var expected in rules)
        {
            if (!expected.Enabled) continue;
            if (index >= expectedCount) return false;
            var actual = list[index++];
            var type = expected.IncludeSubdomains ? "DomainSuffix" : "Domain";
            if (actual.ValueKind != JsonValueKind.Object
                || !actual.TryGetProperty("type", out var actualType) || actualType.GetString() != type
                || !actual.TryGetProperty("payload", out var domain) || domain.GetString() != expected.Domain
                || !actual.TryGetProperty("proxy", out var target) || target.GetString() != ProfileService.CustomRuleProxy(provider, expected)
                || (actual.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object
                    && extra.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True))
                return false;
        }
        return true;
    }

    internal async Task ProbeProxyAsync(string proxy, string url, CancellationToken cancellationToken)
    {
        var endpoint = $"proxies/{Uri.EscapeDataString(proxy)}/delay?timeout=7000&url={Uri.EscapeDataString(url)}&expected=200-399";
        using var result = await GetAsync(endpoint, cancellationToken, TimeSpan.FromSeconds(9));
        if (!result.RootElement.TryGetProperty("delay", out var delay) || !delay.TryGetInt32(out var milliseconds) || milliseconds <= 0)
            throw new HttpRequestException("Mihomo did not confirm the requested outbound probe.");
    }

    private async Task<JsonDocument> GetAsync(string endpoint, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? ControllerTimeout);
        using var response = await _http.GetAsync(endpoint, deadline.Token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
    }
    public void Dispose() => _http.Dispose();
}
