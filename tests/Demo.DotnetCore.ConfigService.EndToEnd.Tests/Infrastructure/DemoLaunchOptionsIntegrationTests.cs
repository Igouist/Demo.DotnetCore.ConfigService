namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

[Collection(EndToEndCollection.Name)]
public sealed class DemoLaunchOptionsIntegrationTests(PackagedDemoFixture fixture)
{
    [Fact]
    public Task Environment_overrides_are_applied_to_the_Demo_process() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(Environment_overrides_are_applied_to_the_Demo_process),
            async scenario =>
            {
                await SetRequiredValueAsync(
                    scenario,
                    scenario.CentralEnvironment,
                    "required-value");
                await scenario.StartDemoAndWaitUntilReadyAsync(new DemoLaunchOptions(
                    EnvironmentOverrides: new Dictionary<string, string>
                    {
                        ["Demo__LocalOnlyMessage"] = "environment-override"
                    }));

                Assert.Equal(
                    "environment-override",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("LocalOnlyMessage"));
            });

    [Fact]
    public Task Command_line_overrides_are_applied_to_the_Demo_process() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(Command_line_overrides_are_applied_to_the_Demo_process),
            async scenario =>
            {
                await SetRequiredValueAsync(
                    scenario,
                    scenario.CentralEnvironment,
                    "required-value");
                await scenario.StartDemoAndWaitUntilReadyAsync(new DemoLaunchOptions(
                    CommandLineOverrides: new Dictionary<string, string>
                    {
                        ["Demo:LocalOnlyMessage"] = "command-line-override"
                    }));

                Assert.Equal(
                    "command-line-override",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("LocalOnlyMessage"));
            });

    [Fact]
    public Task Central_environment_override_selects_the_requested_snapshot() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(Central_environment_override_selects_the_requested_snapshot),
            async scenario =>
            {
                const string launchEnvironment = "LaunchOverride";
                await SetRequiredValueAsync(
                    scenario,
                    launchEnvironment,
                    "alternate-environment-value");
                await scenario.StartDemoAndWaitUntilReadyAsync(
                    new DemoLaunchOptions(CentralEnvironment: launchEnvironment));

                Assert.Equal(
                    "alternate-environment-value",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
            });

    private static async Task SetRequiredValueAsync(
        PocProcessEnvironment scenario,
        string centralEnvironment,
        string value)
    {
        await scenario.StartCentralAsync();
        await scenario.Management.SetAsync(
            centralEnvironment,
            "Demo:CentralRequiredMessage",
            value);
    }
}
