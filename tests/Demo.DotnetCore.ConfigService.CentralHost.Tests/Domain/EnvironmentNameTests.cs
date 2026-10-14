namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Domain;

public sealed class EnvironmentNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" UAT")]
    [InlineData("UAT ")]
    public void Environment_rejects_invalid_text(string? candidate)
    {
        Assert.False(EnvironmentName.TryCreate(candidate, out _));
    }

    [Fact]
    public void Environment_equality_is_ordinal_case_insensitive_and_keeps_display_text()
    {
        var first = EnvironmentName.FromValidated("UAT");
        var second = EnvironmentName.FromValidated("uat");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("UAT", first.Value);
        Assert.Equal("uat", second.Value);
    }

    [Theory]
    [InlineData("Straße", "STRAẞE")]
    [InlineData("Ångström", "ångström")]
    public void Environment_non_ascii_equality_matches_ordinal_case_insensitive_comparer(string firstText, string secondText)
    {
        var first = EnvironmentName.FromValidated(firstText);
        var second = EnvironmentName.FromValidated(secondText);

        Assert.Equal(StringComparer.OrdinalIgnoreCase.Equals(firstText, secondText), first.Equals(second));
        Assert.Equal(firstText, first.Value);
        Assert.Equal(secondText, second.Value);
    }
}
