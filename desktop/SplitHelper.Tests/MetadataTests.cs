using SplitHelper.Services;
using YamlDotNet.RepresentationModel;

namespace SplitHelper.Tests;

public sealed class MetadataTests
{
    [Theory]
    [InlineData("Mullvad")]
    [InlineData("Windscribe")]
    public void Merge_selects_stable_local_profile_with_separate_script_and_preserves_other_data(string provider)
    {
        const string input = """
            current: unrelated
            unknown-root:
              nested: [one, two]
            items:
              - uid: unrelated
                type: remote
                file: other.yaml
                option:
                  script: custom-script
                  extra: true
            """;
        var original = ProfileService.Parse(input);
        var merged = ProfileService.Parse(ProfileService.MergeMetadata(input, provider));
        Assert.Equal($"splithelper-{provider.ToLowerInvariant()}-v1", ProfileService.Value(merged, "current"));
        ProfileServiceTests.AssertYamlEqual(ProfileService.Node(original, "unknown-root")!, ProfileService.Node(merged, "unknown-root")!);
        var items = Items(merged);
        ProfileServiceTests.AssertYamlEqual(Items(original).Single(), items.Single(item => ProfileService.Value(item, "uid") == "unrelated"));
        var local = items.Single(item => ProfileService.Value(item, "uid") == ProfileService.ProfileUid(provider));
        var script = items.Single(item => ProfileService.Value(item, "uid") == ProfileService.ScriptUid(provider));
        Assert.Equal("local", ProfileService.Value(local, "type"));
        Assert.Equal("script", ProfileService.Value(script, "type"));
        Assert.Equal(ProfileService.ScriptUid(provider), ProfileService.Value(Assert.IsType<YamlMappingNode>(ProfileService.Node(local, "option")), "script"));
        Assert.Equal(ProfileService.ScriptUid(provider) + ".js", ProfileService.Value(script, "file"));
        Assert.Equal(ProfileService.ProfileUid(provider) + ".yaml", ProfileService.Value(local, "file"));
        Assert.Equal(ProfileService.Ownership, ProfileService.Value(local, "desc"));

        // Existing local-only options must survive subsequent imports too.
        var option = Assert.IsType<YamlMappingNode>(ProfileService.Node(local, "option"));
        ProfileService.Set(option, "future-option", "keep-me");
        var repeated = ProfileService.Parse(ProfileService.MergeMetadata(ProfileService.Dump(merged), provider));
        Assert.Equal(3, Items(repeated).Count);
        var repeatedLocal = Items(repeated).Single(item => ProfileService.Value(item, "uid") == ProfileService.ProfileUid(provider));
        Assert.Equal("keep-me", ProfileService.Value(Assert.IsType<YamlMappingNode>(ProfileService.Node(repeatedLocal, "option")), "future-option"));
    }

    [Theory]
    [InlineData("Mullvad", false)]
    [InlineData("Mullvad", true)]
    [InlineData("Windscribe", false)]
    [InlineData("Windscribe", true)]
    public void Merge_refuses_uid_collision_with_unowned_description_or_file(string provider, bool script)
    {
        var uid = script ? ProfileService.ScriptUid(provider) : ProfileService.ProfileUid(provider);
        var extension = script ? ".js" : ".yaml";
        var foreignDescription = $"items:\n- uid: {uid}\n  desc: Someone else\n  file: {uid}{extension}\n";
        var foreignFile = $"items:\n- uid: {uid}\n  desc: {ProfileService.Ownership}\n  file: unrelated{extension}\n";
        Assert.Throws<UserError>(() => ProfileService.MergeMetadata(foreignDescription, provider));
        Assert.Throws<UserError>(() => ProfileService.MergeMetadata(foreignFile, provider));
    }

    [Fact]
    public void Merge_refuses_duplicate_managed_uid()
    {
        var text = ProfileService.MergeMetadata("items: []", "Windscribe");
        var root = ProfileService.Parse(text);
        var items = Assert.IsType<YamlSequenceNode>(ProfileService.Node(root, "items"));
        // Parse a separate copy so the YAML emitter cannot replace the duplicate with an alias.
        items.Add(Items(ProfileService.Parse(text)).First());
        Assert.Throws<UserError>(() => ProfileService.MergeMetadata(ProfileService.Dump(root), "Windscribe"));
    }

    private static List<YamlMappingNode> Items(YamlMappingNode root) =>
        Assert.IsType<YamlSequenceNode>(ProfileService.Node(root, "items")).Children.Cast<YamlMappingNode>().ToList();
}
