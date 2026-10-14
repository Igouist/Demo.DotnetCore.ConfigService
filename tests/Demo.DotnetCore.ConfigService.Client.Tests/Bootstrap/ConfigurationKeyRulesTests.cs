namespace Demo.DotnetCore.ConfigService.Client.Tests.Bootstrap;

public sealed class ConfigurationKeyRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Demo")]
    [InlineData("Demo ")]
    [InlineData(":Demo")]
    [InlineData("Demo:")]
    [InlineData("Demo::Message")]
    [InlineData("Demo: :Message")]
    [InlineData("CentralConfig")]
    [InlineData("centralconfig:Enabled")]
    public void Invalid_configuration_keys_are_rejected(string? candidate) =>
        Assert.False(ConfigurationKeyRules.IsValid(candidate));

    [Theory]
    [InlineData("Demo")]
    [InlineData("Demo:Message")]
    [InlineData("示範:訊息")]
    [InlineData("Demo__Message")]
    [InlineData("Demo: Value")]
    public void Supported_configuration_keys_are_preserved(string candidate) =>
        Assert.True(ConfigurationKeyRules.IsValid(candidate));
}
