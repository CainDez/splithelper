namespace SplitHelper;

public sealed class AppSettings
{
    public string Provider { get; set; } = "Windscribe";
    public string GatewayHost { get; set; } = "";
    public int GatewayPort { get; set; } = 61053;
    [System.Text.Json.Serialization.JsonIgnore]
    public string GatewayUsername { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string GatewayPassword { get; set; } = "";
    public string ProtectedCredentials { get; set; } = "";
    public int MixedPort { get; set; } = 7897;
    public bool AutoEnable { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool WindscribeConfirmed { get; set; }
    public string WindscribeInterface { get; set; } = "";
    public string ConfirmedHelperPath { get; set; } = "";
    public bool AllowLan { get; set; } = true;
}

public sealed record CheckItem(string Title, string Detail, string Tone);
public sealed record CustomRule(string Domain, bool IncludeSubdomains = true, string Target = "VPN", bool Enabled = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string MatchLabel => IncludeSubdomains ? "域名及子域名" : "仅此域名";
    [System.Text.Json.Serialization.JsonIgnore]
    public string TargetLabel => Target switch { "ChinaDirect" => "国内直连", "VPN" => "VPN", "REJECT" => "拦截", _ => Target };
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusLabel => Enabled ? "启用" : "停用";
}
public sealed record HealthReport(string Headline, string Subtitle, string Tone, bool Active, string DomesticStatus, string OverseasStatus, IReadOnlyList<CheckItem> Checks);
public sealed class UserError(string message) : Exception(message);

public sealed class AppPaths
{
    public string BaseDirectory { get; }
    public string DataDirectory { get; }
    public string HelperDirectory => Path.Combine(BaseDirectory, "components", "direct-helper");
    public string HelperExe => Path.Combine(HelperDirectory, "mullvad-direct-helper.exe");
    public string HelperConfig => Path.Combine(HelperDirectory, "direct.yaml");
    public string HelperManifest => Path.Combine(HelperDirectory, "manifest.json");
    public string MainCoreDirectory => Path.Combine(BaseDirectory, "components", "main-core");
    public string MainCoreExe => Path.Combine(MainCoreDirectory, "split-routing-core.exe");
    public string MainRuntimeDirectory => Path.Combine(DataDirectory, "main-runtime");
    public string MainConfig => Path.Combine(MainRuntimeDirectory, "config.yaml");
    public string CustomRulesFile => Path.Combine(DataDirectory, "custom-rules.json");
    public string PendingCustomRulesFile => CustomRulesFile + ".pending";
    public string MainManifest => Path.Combine(MainCoreDirectory, "manifest.json");
    public string MainPipeName => "splithelper-main-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24].ToLowerInvariant();
    public string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");
    public string GeneratedDirectory => Path.Combine(DataDirectory, "generated");
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string ProxySessionFile => Path.Combine(DataDirectory, "proxy-session.dat");
    public AppPaths(string? baseDirectory = null, string? dataDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitHelper"));
    }
}
