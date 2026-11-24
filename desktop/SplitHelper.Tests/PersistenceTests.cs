using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void Save_encrypts_credentials_with_current_user_dpapi_and_helper_move_invalidates_confirmation()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("install-a"), temp.Child("data"));
        var store = new SettingsStore(paths);
        var settings = new AppSettings
        {
            GatewayUsername = "private-test-user-" + Guid.NewGuid(),
            GatewayPassword = "private-test-password-" + Guid.NewGuid(),
            WindscribeConfirmed = true, MixedPort = 23456
        };
        Assert.False(store.Exists);
        store.Save(settings);
        var json = File.ReadAllText(paths.SettingsFile);
        Assert.DoesNotContain(settings.GatewayUsername, json);
        Assert.DoesNotContain(settings.GatewayPassword, json);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("GatewayUsername", out _));
        Assert.False(document.RootElement.TryGetProperty("GatewayPassword", out _));
        var protectedValue = document.RootElement.GetProperty("ProtectedCredentials").GetString()!;
        var decoded = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Encoding.UTF8.GetBytes("SplitHelper.Desktop.v1"), DataProtectionScope.CurrentUser));
        using var credentials = JsonDocument.Parse(decoded);
        Assert.Equal(settings.GatewayUsername, credentials.RootElement.GetProperty("Username").GetString());
        Assert.Equal(settings.GatewayPassword, credentials.RootElement.GetProperty("Password").GetString());
        var loaded = store.Load();
        Assert.Equal(settings.GatewayUsername, loaded.GatewayUsername);
        Assert.Equal(settings.GatewayPassword, loaded.GatewayPassword);
        Assert.True(loaded.WindscribeConfirmed);
        Assert.Equal(23456, loaded.MixedPort);
        var moved = new SettingsStore(new AppPaths(temp.Child("install-b"), paths.DataDirectory)).Load();
        Assert.False(moved.WindscribeConfirmed);
        Assert.Equal(settings.GatewayPassword, moved.GatewayPassword);
    }

    [Fact]
    public void Load_invalid_protected_credentials_clears_credentials_and_confirmation()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("install"), temp.Child("data"));
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(new AppSettings
        {
            ProtectedCredentials = "invalid-base64!", WindscribeConfirmed = true, ConfirmedHelperPath = paths.HelperExe
        }));
        var loaded = new SettingsStore(paths).Load();
        Assert.Empty(loaded.GatewayUsername);
        Assert.Empty(loaded.GatewayPassword);
        Assert.Empty(loaded.ProtectedCredentials);
        Assert.False(loaded.WindscribeConfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_rejects_corrupted_helper_or_config_without_starting_process(bool corruptConfig)
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("install"), temp.Child("data"));
        Directory.CreateDirectory(paths.HelperDirectory);
        var exe = Encoding.UTF8.GetBytes("This fixture is data, not an executable.");
        var config = Encoding.UTF8.GetBytes("mode: direct\n");
        await File.WriteAllBytesAsync(paths.HelperExe, exe);
        await File.WriteAllBytesAsync(paths.HelperConfig, config);
        await File.WriteAllTextAsync(paths.HelperManifest, JsonSerializer.Serialize(new HelperManifest("test", Convert.ToHexString(SHA256.HashData(exe)), Convert.ToHexString(SHA256.HashData(config)))));
        using var helper = new HelperManager(paths);
        await helper.VerifyAsync(CancellationToken.None);
        await File.AppendAllTextAsync(corruptConfig ? paths.HelperConfig : paths.HelperExe, "tampered");
        await Assert.ThrowsAsync<UserError>(() => helper.VerifyAsync(CancellationToken.None));
    }
}

internal sealed class TempDirectory : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SplitHelper.Tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(_root);
    public string Child(string name)
    {
        var result = Path.GetFullPath(Path.Combine(_root, name));
        if (!result.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test path escaped its temporary directory.");
        return result;
    }
    public void Dispose()
    {
        var target = Path.GetFullPath(_root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("SplitHelper.Tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
