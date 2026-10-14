using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.DemoHost.Tests.Configuration;

public sealed class EffectiveConfigurationReaderTests
{
    [Fact]
    public void Demo_values_are_relative_effective_non_null_values_below_the_section()
    {
        IConfiguration configuration = Build(
            ("Demo", "root-must-not-appear"),
            ("Demo:Empty", string.Empty),
            ("Demo:Parent", "parent-value"),
            ("Demo:Parent:Child", "child-value"),
            ("Demo:Group:Leaf", "leaf-value"),
            ("Outside:Value", "outside-value"));

        var reader = new EffectiveConfigurationReader(configuration);

        IReadOnlyDictionary<string, string> values = reader.ReadDemoValues();

        Assert.Equal(4, values.Count);
        Assert.Equal(string.Empty, values["empty"]);
        Assert.Equal("parent-value", values["PARENT"]);
        Assert.Equal("child-value", values["parent:child"]);
        Assert.Equal("leaf-value", values["GROUP:LEAF"]);
        Assert.DoesNotContain("Demo", values.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Outside:Value", values.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Group", values.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_values_retain_full_paths_and_filter_only_null_nodes()
    {
        IConfiguration configuration = Build(
            ("Demo", "root-value"),
            ("Demo:Empty", string.Empty),
            ("Demo:Parent:Child", "child-value"),
            ("Outside:Value", "outside-value"));

        var reader = new EffectiveConfigurationReader(configuration);

        IReadOnlyDictionary<string, string> values = reader.ReadRootValues();

        Assert.Equal("root-value", values["demo"]);
        Assert.Equal(string.Empty, values["DEMO:EMPTY"]);
        Assert.Equal("child-value", values["Demo:Parent:Child"]);
        Assert.Equal("outside-value", values["outside:value"]);
        Assert.DoesNotContain("Demo:Parent", values.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Outside", values.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_demo_section_returns_an_empty_dictionary()
    {
        var reader = new EffectiveConfigurationReader(Build(("Outside", "value")));

        Assert.Empty(reader.ReadDemoValues());
    }

    private static IConfiguration Build(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(static item => item.Key, static item => item.Value))
            .Build();
}
