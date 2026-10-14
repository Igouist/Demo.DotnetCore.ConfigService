namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Domain;

public sealed class ConfigurationValueLimitsTests
{
    [Fact]
    public void Value_limit_accepts_16384_utf8_bytes_and_rejects_16385()
    {
        Assert.True(ConfigurationValueLimits.IsValid(new string('a', 16_384)));
        Assert.False(ConfigurationValueLimits.IsValid(new string('a', 16_385)));
    }

    [Fact]
    public void Value_limit_counts_multibyte_utf8_and_accepts_empty_string()
    {
        Assert.True(ConfigurationValueLimits.IsValid(string.Empty));
        Assert.True(ConfigurationValueLimits.IsValid(new string('界', 5_461) + "a"));
        Assert.False(ConfigurationValueLimits.IsValid(new string('界', 5_462)));
        Assert.False(ConfigurationValueLimits.IsValid(null));
    }
}
