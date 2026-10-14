using System.Text;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Bootstrap;

public sealed class CentralConfigBootstrapReaderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-bool")]
    public void Enabled_must_be_explicit_and_boolean(string? enabled)
    {
        IConfiguration configuration = Build(("CentralConfig:Enabled", enabled));

        BootstrapReadResult result = CentralConfigBootstrapReader.Read(configuration);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Disabled_mode_ignores_every_other_invalid_setting()
    {
        IConfiguration configuration = Build(
            ("CentralConfig:Enabled", "false"),
            ("CentralConfig:BaseAddress", "not a URI"),
            ("CentralConfig:Environment", " "),
            ("CentralConfig:CentralRequiredKeys:0", "CentralConfig:Enabled"));

        BootstrapReadResult result = CentralConfigBootstrapReader.Read(configuration);

        Assert.True(result.IsValid);
        Assert.False(result.IsEnabled);
        Assert.Null(result.EnabledBootstrap);
    }

    [Theory]
    [InlineData("ftp://central/", false)]
    [InlineData("https:///missing-host", false)]
    [InlineData("https://user:password@central/", false)]
    [InlineData("https://central/path?x=1", false)]
    [InlineData("https://central/path#fragment", false)]
    [InlineData("http://central/config-center", true)]
    [InlineData("https://central:8443/config-center/", true)]
    public void Base_address_rules_are_enforced(string baseAddress, bool expectedValid)
    {
        IConfiguration configuration = EnabledConfiguration(baseAddress, "UAT", "Demo:Required");

        BootstrapReadResult result = CentralConfigBootstrapReader.Read(configuration);

        Assert.Equal(expectedValid, result.IsValid);
        if (expectedValid)
        {
            Assert.Equal(
                new Uri(baseAddress.TrimEnd('/') + "/api/v1/distribution/configurations/resolve"),
                result.EnabledBootstrap!.ResolveEndpoint);
        }
    }

    [Fact]
    public void Environment_is_explicit_and_independent_from_host_environment()
    {
        IConfiguration configuration = EnabledConfiguration("https://central/", "UAT", "Demo:Required");

        BootstrapReadResult result = CentralConfigBootstrapReader.Read(configuration);

        Assert.Equal("UAT", result.EnabledBootstrap!.Environment);
    }

    [Theory]
    [InlineData(
        "CentralConfig:CentralRequiredKeys",
        "Demo:Scalar",
        "CentralConfig:FallbackAllowedKeys:0",
        "Demo:Fallback")]
    [InlineData(
        "CentralConfig:FallbackAllowedKeys",
        "Demo:Scalar",
        "CentralConfig:CentralRequiredKeys:0",
        "Demo:Required")]
    [InlineData(
        "CentralConfig:CentralRequiredKeys:Named",
        "Demo:Named",
        "CentralConfig:FallbackAllowedKeys:0",
        "Demo:Fallback")]
    [InlineData(
        "CentralConfig:CentralRequiredKeys:1",
        "Demo:Sparse",
        "CentralConfig:FallbackAllowedKeys:0",
        "Demo:Fallback")]
    [InlineData(
        "CentralConfig:CentralRequiredKeys:-1",
        "Demo:Negative",
        "CentralConfig:FallbackAllowedKeys:0",
        "Demo:Fallback")]
    [InlineData(
        "CentralConfig:CentralRequiredKeys:00",
        "Demo:LeadingZero",
        "CentralConfig:FallbackAllowedKeys:0",
        "Demo:Fallback")]
    [InlineData(
        "CentralConfig:CentralRequiredKeys:+0",
        "Demo:NonCanonical",
        "CentralConfig:FallbackAllowedKeys:0",
        "Demo:Fallback")]
    public void Enabled_mode_rejects_scalar_or_noncanonical_effective_array_shapes(
        string malformedKey,
        string malformedValue,
        string validKey,
        string validValue)
    {
        IConfiguration configuration = Build(
            ("CentralConfig:Enabled", "true"),
            ("CentralConfig:BaseAddress", "https://central/"),
            ("CentralConfig:Environment", "UAT"),
            (malformedKey, malformedValue),
            (validKey, validValue));

        BootstrapReadResult result = CentralConfigBootstrapReader.Read(configuration);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Base_json_arrays_accept_ordinary_provider_overrides_when_effective_indices_remain_canonical()
    {
        const string baseJson =
            """
            {
              "CentralConfig": {
                "Enabled": true,
                "BaseAddress": "https://central/",
                "Environment": "UAT",
                "CentralRequiredKeys": [ "Demo:First", "Demo:BaseSecond" ],
                "FallbackAllowedKeys": [ "Demo:BaseFallback" ]
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(baseJson));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralConfig:CentralRequiredKeys:1"] = "Demo:OverriddenSecond",
                ["CentralConfig:FallbackAllowedKeys:0"] = "Demo:OverriddenFallback"
            })
            .Build();

        BootstrapReadResult result = CentralConfigBootstrapReader.Read(configuration);

        Assert.True(result.IsValid);
        Assert.Equal(
            ["Demo:First", "Demo:OverriddenSecond", "Demo:OverriddenFallback"],
            result.EnabledBootstrap!.KeySelection.RequestKeys);
    }

    private static IConfiguration EnabledConfiguration(string address, string environment, string key) =>
        Build(
            ("CentralConfig:Enabled", "true"),
            ("CentralConfig:BaseAddress", address),
            ("CentralConfig:Environment", environment),
            ("CentralConfig:CentralRequiredKeys:0", key));

    private static IConfiguration Build(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(entry =>
                new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();
}
