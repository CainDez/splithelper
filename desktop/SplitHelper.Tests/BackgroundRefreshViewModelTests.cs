using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class BackgroundRefreshViewModelTests
{
    [Fact]
    public async Task Background_maintenance_reports_errors_without_disabling_foreground_commands()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        using var model = new DesktopViewModel(paths, false);
        // An unfinished session with no main configuration fails before it touches Windows proxy settings.
        var original = new ProxySnapshot(1, "", "", "");
        var applied = original with { Flags = 3, Server = "127.0.0.1:17898" };
        SettingsStore.WriteProtected(paths.ProxySessionFile, new ProxySession(original, applied, 17898));
        var busyChanged = false;
        var refreshWasDisabled = false;
        var cancelWasEnabled = false;
        model.PropertyChanged += (_, change) =>
        {
            busyChanged |= change.PropertyName == nameof(DesktopViewModel.IsBusy);
            refreshWasDisabled |= !model.RefreshCommand.CanExecute(null);
            cancelWasEnabled |= model.CancelCommand.CanExecute(null);
        };

        await model.RefreshBackgroundAsync();

        Assert.Equal("操作需要处理", model.Headline);
        Assert.NotEmpty(model.Notice);
        Assert.False(busyChanged);
        Assert.False(refreshWasDisabled);
        Assert.False(cancelWasEnabled);
    }

    [Fact]
    public async Task Equivalent_refresh_keeps_check_list_and_changed_results_replace_it_once()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        using var model = new DesktopViewModel(paths, true);
        await model.InitializeAsync();
        var initial = model.Checks;
        var changes = 0;
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(DesktopViewModel.Checks)) changes++;
        };

        await model.InitializeAsync();
        Assert.Same(initial, model.Checks);
        Assert.Equal(0, changes);

        Directory.CreateDirectory(paths.MainRuntimeDirectory);
        File.WriteAllText(Path.Combine(paths.MainRuntimeDirectory, "geo-update.ok"), "");
        await model.InitializeAsync();

        Assert.Equal(1, changes);
        Assert.Contains(model.Checks, item => item.Title == "GeoSite / GeoIP" && item.Tone == "good");
    }
}
