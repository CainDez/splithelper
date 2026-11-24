using System.Security.Cryptography;

namespace SplitHelper.Services;

public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SplitHelper.Desktop.v1");
    public bool Exists => File.Exists(paths.SettingsFile);
    public AppSettings Load()
    {
        if (!Exists) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), JsonOptions) ?? new();
        if (settings.Provider is not ("Mullvad" or "Windscribe")) settings.Provider = "Windscribe";
        if (settings.ProtectedCredentials.Length > 0)
        {
            try
            {
                var credentials = JsonSerializer.Deserialize<GatewayCredentials>(UnprotectText(settings.ProtectedCredentials), JsonOptions);
                settings.GatewayUsername = credentials?.Username ?? "";
                settings.GatewayPassword = credentials?.Password ?? "";
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
            { settings.ProtectedCredentials = ""; settings.GatewayUsername = ""; settings.GatewayPassword = ""; settings.WindscribeConfirmed = false; }
        }
        if (!string.Equals(settings.ConfirmedHelperPath, paths.HelperExe, StringComparison.OrdinalIgnoreCase)) settings.WindscribeConfirmed = false;
        return settings;
    }
    public void Save(AppSettings settings)
    {
        settings.ProtectedCredentials = ProtectText(JsonSerializer.Serialize(new GatewayCredentials(settings.GatewayUsername, settings.GatewayPassword)));
        settings.ConfirmedHelperPath = settings.WindscribeConfirmed ? paths.HelperExe : "";
        Directory.CreateDirectory(paths.DataDirectory);
        AtomicWrite(paths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }
    public static string ProtectText(string text) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
    public static string UnprotectText(string encoded) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser));
    public static void WriteProtected<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, ProtectText(JsonSerializer.Serialize(value, JsonOptions)));
    }
    public static T? ReadProtected<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(UnprotectText(File.ReadAllText(path)), JsonOptions) : default;
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record GatewayCredentials(string Username, string Password);
}
