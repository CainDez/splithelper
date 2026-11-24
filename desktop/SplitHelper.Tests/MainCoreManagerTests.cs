using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class MainCoreManagerTests
{
    [Theory]
    [InlineData("split-routing-core.exe")]
    [InlineData("geosite.dat")]
    [InlineData("Country.mmdb")]
    public async Task Verification_rejects_modified_core_or_database(string changed)
    {
        using var temp = new TempDirectory();
        var paths = CreateComponent(temp);
        using var client = new MihomoClient(paths.MainPipeName);
        using var manager = new MainCoreManager(paths, client);
        await manager.VerifyAsync(default);
        Assert.Equal("test-version", manager.Version);
        File.AppendAllText(Path.Combine(paths.MainCoreDirectory, changed), "modified");
        await Assert.ThrowsAsync<UserError>(() => manager.VerifyAsync(default));
    }

    [Fact]
    public async Task Start_requires_prepared_config_and_rejects_controller_or_listener_changes()
    {
        using var temp = new TempDirectory();
        var paths = CreateComponent(temp);
        using var client = new MihomoClient(paths.MainPipeName);
        using var manager = new MainCoreManager(paths, client);
        await Assert.ThrowsAsync<UserError>(() => manager.StartAsync(new AppSettings(), default));
        Directory.CreateDirectory(paths.MainRuntimeDirectory);
        File.WriteAllText(paths.MainConfig, ProfileService.GenerateStandalone(new AppSettings(), paths.MainPipeName) + "external-controller: 0.0.0.0:9090\n");
        await Assert.ThrowsAsync<UserError>(() => manager.StartAsync(new AppSettings(), default));
        Assert.False(File.Exists(Path.Combine(paths.MainRuntimeDirectory, "process.json")));
    }

    [Fact]
    public async Task Startup_keeps_downloaded_database_and_seeds_only_the_missing_one()
    {
        using var temp = new TempDirectory();
        var paths = CreateComponent(temp);
        using var client = new MihomoClient(paths.MainPipeName);
        using var manager = new MainCoreManager(paths, client);
        Directory.CreateDirectory(paths.MainRuntimeDirectory);
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var settings = new AppSettings { MixedPort = ((IPEndPoint)port.LocalEndpoint).Port };
        port.Stop();
        File.WriteAllText(paths.MainConfig, ProfileService.GenerateStandalone(settings, paths.MainPipeName));
        var updatedGeoSite = Path.Combine(paths.MainRuntimeDirectory, "geosite.dat");
        File.WriteAllText(updatedGeoSite, "newer-download");

        // The test component is deliberately not an executable. The normal start path
        // prepares the runtime data before attempting to launch it.
        await Assert.ThrowsAsync<Win32Exception>(() => manager.StartAsync(settings, default));

        Assert.Equal("newer-download", File.ReadAllText(updatedGeoSite));
        Assert.Equal("test-Country.mmdb", File.ReadAllText(Path.Combine(paths.MainRuntimeDirectory, "Country.mmdb")));
    }

    [Theory]
    [InlineData(204, true)]
    [InlineData(503, false)]
    public async Task Geo_update_remembers_success_and_does_not_immediately_retry_failure(int status, bool succeeded)
    {
        using var temp = new TempDirectory();
        var paths = CreateComponent(temp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = MihomoClientTests.ServeAsync(paths.MainPipeName, [new("/configs/geo", "", status, "POST")], timeout.Token);
        using var client = new MihomoClient(paths.MainPipeName);
        using var manager = new MainCoreManager(paths, client);

        await manager.CheckGeoUpdateAsync().WaitAsync(timeout.Token);
        await server;
        Assert.Equal(succeeded, File.Exists(Path.Combine(paths.MainRuntimeDirectory, "geo-update.ok")));
        Assert.Equal(succeeded ? "good" : "attention", manager.GeoUpdateStatus().Tone);
        Assert.Same(Task.CompletedTask, manager.CheckGeoUpdateAsync());

        if (succeeded)
        {
            using var reopened = new MainCoreManager(paths, client);
            Assert.Same(Task.CompletedTask, reopened.CheckGeoUpdateAsync());
        }
    }

    [Fact]
    public void Stop_never_terminates_a_process_with_another_path_or_start_time()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        Directory.CreateDirectory(paths.MainRuntimeDirectory);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        using var client = new MihomoClient(paths.MainPipeName);
        using var manager = new MainCoreManager(paths, client);
        foreach (var identity in new[] { new HelperInstance(current.Id, current.MainModule!.FileName!, current.StartTime.ToUniversalTime()), new HelperInstance(current.Id, paths.MainCoreExe, DateTime.MinValue) })
        {
            File.WriteAllText(Path.Combine(paths.MainRuntimeDirectory, "process.json"), JsonSerializer.Serialize(identity));
            Assert.False(manager.OwnListenerReady(7897));
            manager.StopOwn();
            Assert.False(current.HasExited);
        }
    }

    private static AppPaths CreateComponent(TempDirectory temp)
    {
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        Directory.CreateDirectory(paths.MainCoreDirectory);
        foreach (var file in new[] { "split-routing-core.exe", "geosite.dat", "Country.mmdb" }) File.WriteAllText(Path.Combine(paths.MainCoreDirectory, file), "test-" + file);
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(paths.MainCoreDirectory, file))));
        File.WriteAllText(paths.MainManifest, JsonSerializer.Serialize(new MainCoreManifest("test-version", Hash("split-routing-core.exe"), Hash("geosite.dat"), Hash("Country.mmdb"))));
        return paths;
    }
}
