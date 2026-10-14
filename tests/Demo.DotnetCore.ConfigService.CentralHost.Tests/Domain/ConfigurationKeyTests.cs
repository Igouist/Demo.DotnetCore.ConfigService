namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Domain;

public sealed class ConfigurationKeyTests
{
    [Theory]
    [InlineData(":Demo")]
    [InlineData("Demo:")]
    [InlineData("Demo::Message")]
    [InlineData("Demo: :Message")]
    [InlineData(" Demo:Message")]
    [InlineData("Demo:Message ")]
    [InlineData("CentralConfig")]
    [InlineData("centralconfig:BaseAddress")]
    public void Configuration_Key_rejects_invalid_structure(string candidate)
    {
        Assert.False(ConfigurationKey.TryCreate(candidate, out _));
    }

    [Fact]
    public void Identity_equality_is_ordinal_case_insensitive_and_keeps_display_text()
    {
        var first = ConfigurationKey.FromValidated("Demo:Message");
        var second = ConfigurationKey.FromValidated("demo:message");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("Demo:Message", first.Value);
        Assert.Equal("demo:message", second.Value);
    }

    [Theory]
    [InlineData("Demo")]
    [InlineData("界:設定")]
    [InlineData("Demo-Config:Message.Name")]
    [InlineData("Demo__Message")]
    [InlineData("Demo:Message")]
    public void Configuration_key_accepts_permitted_text_without_translation(string candidate)
    {
        Assert.True(ConfigurationKey.TryCreate(candidate, out var key));
        Assert.Equal(candidate, key.Value);
    }
}
