namespace Demo.DotnetCore.ConfigService.Client.Tests.Bootstrap;

public sealed class KeySelectionTests
{
    [Fact]
    public void Required_then_fallback_declaration_order_is_preserved()
    {
        Assert.True(KeySelection.TryCreate(
            ["Demo:R1", "Demo:R2"],
            ["Demo:F1"],
            out KeySelection? selection));

        Assert.Equal(["Demo:R1", "Demo:R2", "Demo:F1"], selection!.RequestKeys);
        Assert.Equal(["Demo:R1", "Demo:R2"], selection.RequiredKeys.Select(key => key.DeclaredKey));
        Assert.True(selection.Contains("demo:r1"));
    }

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public void Invalid_or_ambiguous_selection_is_rejected(
        IReadOnlyList<string?> required,
        IReadOnlyList<string?> fallback) =>
        Assert.False(KeySelection.TryCreate(required, fallback, out _));

    public static TheoryData<IReadOnlyList<string?>, IReadOnlyList<string?>> InvalidSelections => new()
    {
        { Array.Empty<string?>(), Array.Empty<string?>() },
        { new string?[] { "Demo:A", "demo:a" }, Array.Empty<string?>() },
        { Array.Empty<string?>(), new string?[] { "Demo:A", "demo:a" } },
        { new string?[] { "Demo:A" }, new string?[] { "demo:a" } },
        { new string?[] { "CentralConfig:Enabled" }, Array.Empty<string?>() },
        { new string?[] { null }, Array.Empty<string?>() }
    };
}
