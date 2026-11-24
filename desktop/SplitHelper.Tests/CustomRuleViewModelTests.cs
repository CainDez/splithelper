using SplitHelper.Services;

namespace SplitHelper.Tests;

public sealed class CustomRuleViewModelTests
{
    [Fact]
    public async Task Add_applies_immediately_when_offline_and_cancel_keeps_committed_list()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        using var model = new DesktopViewModel(paths, false);

        model.AddCustomRuleCommand.Execute(null);
        Assert.True(model.IsEditingCustomRule);
        Assert.True(model.RuleIncludeSubdomains);
        model.RuleDomain = "https://VIDEO.example/watch?v=1";
        model.RuleTarget = "ChinaDirect";
        model.SaveCustomRulesCommand.Execute(null);
        await Until(() => File.Exists(paths.CustomRulesFile) && !model.IsEditingCustomRule);

        Assert.Single(model.CustomRules);
        Assert.Equal("video.example", model.CustomRules[0].Domain);
        Assert.Equal("ChinaDirect", model.CustomRules[0].Target);
        Assert.Contains("下次启用", model.Notice);

        model.EditCustomRuleCommand.Execute(null);
        model.RuleDomain = "changed.example";
        model.CancelCustomRuleEditCommand.Execute(null);
        Assert.False(model.IsEditingCustomRule);
        Assert.Equal("video.example", model.CustomRules[0].Domain);
        Assert.Equal("video.example", System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.CustomRulesFile)).RootElement[0].GetProperty("Domain").GetString());
    }

    [Fact]
    public async Task Invalid_or_duplicate_rule_keeps_form_and_committed_list_unchanged()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        using var model = new DesktopViewModel(paths, false);
        model.AddCustomRuleCommand.Execute(null);
        model.RuleDomain = "example.com";
        model.SaveCustomRulesCommand.Execute(null);
        await Until(() => model.CustomRules.Count == 1 && !model.IsEditingCustomRule);

        model.AddCustomRuleCommand.Execute(null);
        model.RuleDomain = "EXAMPLE.COM";
        model.SaveCustomRulesCommand.Execute(null);
        await Until(() => model.Notice.Contains("重复", StringComparison.Ordinal));
        Assert.True(model.IsEditingCustomRule);
        Assert.Single(model.CustomRules);
        Assert.Contains("重复", model.Notice);

        model.RuleDomain = "example.com,REJECT";
        model.SaveCustomRulesCommand.Execute(null);
        await Until(() => model.Notice.Contains("有效域名", StringComparison.Ordinal));
        Assert.True(model.IsEditingCustomRule);
        Assert.Single(model.CustomRules);
        Assert.Contains("有效域名", model.Notice);
    }

    [Fact]
    public async Task Reorder_and_toggle_are_persisted_as_each_transaction()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Child("app"), temp.Child("data"));
        using var model = new DesktopViewModel(paths, false);
        foreach (var domain in new[] { "one.example", "two.example" })
        {
            model.AddCustomRuleCommand.Execute(null);
            model.RuleDomain = domain;
            model.SaveCustomRulesCommand.Execute(null);
            await Until(() => model.CustomRules.Any(rule => rule.Domain == domain) && !model.IsEditingCustomRule);
        }

        model.SelectedCustomRule = model.CustomRules[1];
        model.MoveCustomRuleUpCommand.Execute(null);
        await Until(() => model.CustomRules[0].Domain == "two.example" && !model.IsBusy);
        model.ToggleCustomRuleCommand.Execute(null);
        await Until(() => !model.CustomRules[0].Enabled && !model.IsBusy);

        var saved = System.Text.Json.JsonSerializer.Deserialize<CustomRule[]>(File.ReadAllText(paths.CustomRulesFile))!;
        Assert.Equal(new[] { "two.example", "one.example" }, saved.Select(rule => rule.Domain));
        Assert.False(saved[0].Enabled);
    }

    [Fact]
    public void Safe_mode_disables_custom_rule_mutations()
    {
        using var temp = new TempDirectory();
        using var model = new DesktopViewModel(new AppPaths(temp.Child("app"), temp.Child("data")), true);
        Assert.False(model.AddCustomRuleCommand.CanExecute(null));
        Assert.False(model.SaveCustomRulesCommand.CanExecute(null));
        Assert.False(model.RemoveCustomRuleCommand.CanExecute(null));
        Assert.False(model.ToggleCustomRuleCommand.CanExecute(null));
        Assert.False(model.MoveCustomRuleUpCommand.CanExecute(null));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Timed out waiting for custom rule operation.");
    }
}
