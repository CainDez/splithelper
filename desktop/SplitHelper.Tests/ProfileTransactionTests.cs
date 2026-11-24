using System.Text.Json;
using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class ProfileTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_import_restores_previous_baseline_and_configuration(bool existingBaseline)
    {
        using var temp = new TempDirectory();
        var baseline = temp.Child("clash-baseline.dat");
        var config = temp.Child("verge.yaml");
        var added = temp.Child("new-profile.yaml");
        var blocked = temp.Child("destination-is-a-directory");
        Directory.CreateDirectory(blocked);
        var originalRecord = SettingsStore.ProtectText(JsonSerializer.Serialize(new { Port = 23456 }));
        if (existingBaseline) File.WriteAllText(baseline, originalRecord);
        File.WriteAllText(config, "verge_mixed_port: 23456\n");
        var originals = new Dictionary<string, string> { [config] = File.ReadAllText(config) };
        if (existingBaseline) originals[baseline] = originalRecord;
        var updates = new Dictionary<string, string>
        {
            [baseline] = SettingsStore.ProtectText(JsonSerializer.Serialize(new { Port = 23457 })),
            [config] = "verge_mixed_port: 23457\n", [added] = "new-profile", [blocked] = "cannot replace directory"
        };

        var error = Record.Exception(() => ProfileService.WriteWithRollback(updates, originals));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.GetType().FullName);
        Assert.Equal("verge_mixed_port: 23456\n", File.ReadAllText(config));
        Assert.False(File.Exists(added));
        if (existingBaseline) Assert.Equal(originalRecord, File.ReadAllText(baseline));
        else Assert.False(File.Exists(baseline));
        Assert.True(Directory.Exists(blocked));
    }
}
