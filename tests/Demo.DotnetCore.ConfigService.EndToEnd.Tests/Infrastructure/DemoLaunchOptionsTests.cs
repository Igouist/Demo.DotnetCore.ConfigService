namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class DemoLaunchOptionsTests
{
    [Theory]
    [InlineData("CentralConfig:CentralRequiredKeys")]
    [InlineData("CentralConfig__CentralRequiredKeys")]
    [InlineData("CentralConfig:CentralRequiredKeys:0")]
    [InlineData("CentralConfig__CentralRequiredKeys__0")]
    [InlineData("centralconfig__fallbackallowedkeys:3")]
    public void Environment_overrides_cannot_replace_selection_arrays(string key)
    {
        const string value = "environment-sensitive-value";
        var options = new DemoLaunchOptions(
            EnvironmentOverrides: new Dictionary<string, string>
            {
                [key] = value
            });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DemoLaunchOptionsValidator.Validate(options));

        Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CentralConfig:FallbackAllowedKeys")]
    [InlineData("CentralConfig__FallbackAllowedKeys")]
    [InlineData("CentralConfig:FallbackAllowedKeys:0")]
    [InlineData("CentralConfig__FallbackAllowedKeys__0")]
    [InlineData("CENTRALCONFIG:CENTRALREQUIREDKEYS__named")]
    public void Command_line_overrides_cannot_replace_selection_arrays(string key)
    {
        const string value = "command-line-sensitive-value";
        var options = new DemoLaunchOptions(
            CommandLineOverrides: new Dictionary<string, string>
            {
                [key] = value
            });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DemoLaunchOptionsValidator.Validate(options));

        Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Override_values_are_not_misclassified_as_selection_keys()
    {
        const string selectionKeyAsValue = "CentralConfig__CentralRequiredKeys__0";
        var options = new DemoLaunchOptions(
            EnvironmentOverrides: new Dictionary<string, string>
            {
                ["DEMO_SAFE_ENVIRONMENT_NAME"] = selectionKeyAsValue
            },
            CommandLineOverrides: new Dictionary<string, string>
            {
                ["Demo:SafeCommandLineName"] = selectionKeyAsValue
            });

        DemoLaunchOptionsValidator.Validate(options);
    }

    [Fact]
    public void Invalid_override_names_are_rejected_without_echoing_inputs()
    {
        const string value = "invalid-name-sensitive-value";
        var options = new DemoLaunchOptions(
            CommandLineOverrides: new Dictionary<string, string>
            {
                ["  "] = value
            });

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => DemoLaunchOptionsValidator.Validate(options));

        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "urls")]
    [InlineData(false, "contentRoot")]
    [InlineData(false, "CentralConfig__BaseAddress")]
    [InlineData(false, "CentralConfig__Enabled")]
    [InlineData(true, "URLS")]
    [InlineData(true, "CONTENTROOT")]
    [InlineData(true, "centralconfig:baseaddress")]
    [InlineData(true, "centralconfig:enabled")]
    public void Overrides_cannot_replace_harness_owned_launch_settings(
        bool commandLine,
        string key)
    {
        const string value = "harness-setting-sensitive-value";
        IReadOnlyDictionary<string, string> overrides = new Dictionary<string, string>
        {
            [key] = value
        };
        var options = commandLine
            ? new DemoLaunchOptions(CommandLineOverrides: overrides)
            : new DemoLaunchOptions(EnvironmentOverrides: overrides);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DemoLaunchOptionsValidator.Validate(options));

        Assert.DoesNotContain(key, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("ASPNETCORE_CONTENTROOT")]
    [InlineData("DOTNET_URLS")]
    [InlineData("DOTNET_CONTENTROOT")]
    public void Environment_overrides_cannot_replace_harness_owned_hosting_aliases(string key)
    {
        const string value = "hosting-alias-sensitive-value";
        var options = new DemoLaunchOptions(
            EnvironmentOverrides: new Dictionary<string, string>
            {
                [key] = value
            });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DemoLaunchOptionsValidator.Validate(options));

        Assert.DoesNotContain(key, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }
}
