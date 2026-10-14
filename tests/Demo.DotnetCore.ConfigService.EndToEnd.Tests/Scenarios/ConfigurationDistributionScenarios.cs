using System.Net;
using Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Scenarios;

[Collection(EndToEndCollection.Name)]
public sealed class ConfigurationDistributionScenarios(PackagedDemoFixture fixture)
{
    [Fact]
    public async Task A_required_central_value_is_visible_through_the_scoped_endpoint()
    {
        await PocProcessEnvironment.RunAsync(
            fixture,
            nameof(A_required_central_value_is_visible_through_the_scoped_endpoint),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                ManagementCommandResponse receipt = await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "central-required");

                await scenario.StartDemoAndWaitUntilReadyAsync();
                EffectiveConfigurationResponse response =
                    await scenario.GetEffectiveConfigurationAsync();

                Assert.Equal(1, receipt.Version);
                Assert.Equal("Present", receipt.State);
                Assert.Equal("Demo", response.Scope);
                Assert.Equal("central-required", response.GetValue("CentralRequiredMessage"));
            });
    }

    [Fact]
    public Task B_running_demo_keeps_its_snapshot_until_restart() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(B_running_demo_keeps_its_snapshot_until_restart),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "snapshot-v1");
                await scenario.StartDemoAndWaitUntilReadyAsync();

                Assert.Equal(
                    "snapshot-v1",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));

                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "snapshot-v2");
                Assert.Equal(
                    "snapshot-v1",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));

                await scenario.StopDemoAsync();
                await scenario.StartDemoAndWaitUntilReadyAsync();
                Assert.Equal(
                    "snapshot-v2",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
            });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task C_fallback_allowed_key_uses_base_value_when_never_created_or_removed(
        bool removeAnExistingEntry) =>
        PocProcessEnvironment.RunAsync(
            fixture,
            $"{nameof(C_fallback_allowed_key_uses_base_value_when_never_created_or_removed)}-{removeAnExistingEntry}",
            async scenario =>
            {
                await scenario.StartCentralAsync();
                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "required-value");

                if (removeAnExistingEntry)
                {
                    await scenario.Management.SetAsync(
                        scenario.CentralEnvironment,
                        "Demo:FallbackAllowedMessage",
                        "temporary-central-value");
                    await scenario.Management.RemoveAsync(
                        scenario.CentralEnvironment,
                        "Demo:FallbackAllowedMessage");
                }

                await scenario.StartDemoAndWaitUntilReadyAsync();

                Assert.Equal(
                    "local-fallback",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("FallbackAllowedMessage"));
            });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task D_missing_required_key_prevents_demo_startup(
        bool removeAnExistingEntry) =>
        PocProcessEnvironment.RunAsync(
            fixture,
            $"{nameof(D_missing_required_key_prevents_demo_startup)}-{removeAnExistingEntry}",
            async scenario =>
            {
                await scenario.StartCentralAsync();
                if (removeAnExistingEntry)
                {
                    await scenario.Management.SetAsync(
                        scenario.CentralEnvironment,
                        "Demo:CentralRequiredMessage",
                        "temporary-required-value");
                    await scenario.Management.RemoveAsync(
                        scenario.CentralEnvironment,
                        "Demo:CentralRequiredMessage");
                }

                await scenario.StartDemoAsync();
                StartupMarker failed = await scenario.WaitForDemoMarkerAsync(
                    static marker => marker.Event == "failed",
                    "missing-required terminal marker",
                    TimeSpan.FromSeconds(15));
                ChildProcessExit exit = await scenario.WaitForDemoExitAsync(
                    TimeSpan.FromSeconds(15));

                Assert.Equal("MissingRequiredKeys", failed.FailureKind);
                Assert.Equal<int?>(1, failed.AttemptCount);
                Assert.NotEqual(0, exit.ExitCode);
            });

    [Fact]
    public Task E_empty_central_value_remains_an_empty_string() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(E_empty_central_value_remains_an_empty_string),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    string.Empty);
                await scenario.StartDemoAndWaitUntilReadyAsync();

                Assert.Equal(
                    string.Empty,
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
            });

    [Fact]
    public Task F_set_remove_set_preserves_history_and_restores_the_value() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(F_set_remove_set_preserves_history_and_restores_the_value),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                ManagementCommandResponse first = await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "history-v1");
                ManagementCommandResponse removed = await scenario.Management.RemoveAsync(
                    scenario.CentralEnvironment.ToUpperInvariant(),
                    "demo:centralrequiredmessage");
                ManagementCommandResponse restored = await scenario.Management.SetAsync(
                    scenario.CentralEnvironment.ToLowerInvariant(),
                    "DEMO:CENTRALREQUIREDMESSAGE",
                    "history-v3");

                CurrentConfigurationResponse current = await scenario.Management.GetCurrentAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage");
                ConfigurationHistoryResponse history = await scenario.Management.GetHistoryAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage");

                Assert.Equal((1L, "Present"), (first.Version, first.State));
                Assert.Equal((2L, "Removed"), (removed.Version, removed.State));
                Assert.Equal((3L, "Present"), (restored.Version, restored.State));
                Assert.Equal((3L, "Present", "history-v3"),
                    (current.Version, current.State, current.Value));
                Assert.Equal(new long[] { 3L, 2L, 1L },
                    history.Versions.Select(static item => item.Version));
                Assert.Equal(new string[] { "Set", "Remove", "Set" },
                    history.Versions.Select(static item => item.Operation));
                Assert.Equal(new string?[] { "history-v3", null, "history-v1" },
                    history.Versions.Select(static item => item.Value));

                await scenario.StartDemoAndWaitUntilReadyAsync();
                Assert.Equal(
                    "history-v3",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
            });

    [Fact]
    public Task G_environment_and_command_line_override_the_central_snapshot() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(G_environment_and_command_line_override_the_central_snapshot),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "central-value");

                await scenario.StartDemoAndWaitUntilReadyAsync();
                Assert.Equal(
                    "central-value",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
                await scenario.StopDemoAsync();

                var environmentOverrides = new Dictionary<string, string>
                {
                    ["Demo__CentralRequiredMessage"] = "environment-value"
                };
                await scenario.StartDemoAndWaitUntilReadyAsync(
                    new DemoLaunchOptions(EnvironmentOverrides: environmentOverrides));
                Assert.Equal(
                    "environment-value",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
                await scenario.StopDemoAsync();

                var commandLineOverrides = new Dictionary<string, string>
                {
                    ["Demo:CentralRequiredMessage"] = "command-value"
                };
                await scenario.StartDemoAndWaitUntilReadyAsync(
                    new DemoLaunchOptions(
                        EnvironmentOverrides: environmentOverrides,
                        CommandLineOverrides: commandLineOverrides));
                Assert.Equal(
                    "command-value",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
            });

    [Fact]
    public Task H_scoped_and_unsafe_routes_expose_distinct_effective_views() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(H_scoped_and_unsafe_routes_expose_distinct_effective_views),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "central-value");
                var overrides = new Dictionary<string, string>
                {
                    ["Demo__Parent"] = "parent-value",
                    ["Demo__Parent__Child"] = "child-value",
                    ["Demo__Empty"] = string.Empty,
                    ["Outside__Value"] = "outside-value"
                };
                await scenario.StartDemoAndWaitUntilReadyAsync(
                    new DemoLaunchOptions(EnvironmentOverrides: overrides));

                DemoHttpResult<EffectiveConfigurationResponse> scoped =
                    await scenario.GetEffectiveConfigurationResultAsync();
                DemoHttpResult<UnsafeRootConfigurationResponse> root =
                    await scenario.GetUnsafeRootConfigurationResultAsync();

                Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
                Assert.Equal("application/json", scoped.MediaType);
                Assert.True(scoped.HasNoStore);
                Assert.Equal("Demo", scoped.Body.Scope);
                Assert.Equal("central-value", scoped.Body.GetValue("CentralRequiredMessage"));
                Assert.Equal("parent-value", scoped.Body.GetValue("Parent"));
                Assert.Equal("child-value", scoped.Body.GetValue("Parent:Child"));
                Assert.Equal(string.Empty, scoped.Body.GetValue("Empty"));
                Assert.DoesNotContain(
                    scoped.Body.EffectiveValues.Keys,
                    static key => key.StartsWith("Outside", StringComparison.OrdinalIgnoreCase));

                Assert.Equal(HttpStatusCode.OK, root.StatusCode);
                Assert.Equal("application/json", root.MediaType);
                Assert.True(root.HasNoStore);
                Assert.Equal("Root", root.Body.Scope);
                Assert.Equal(
                    "This response may expose sensitive configuration values.",
                    root.Body.Warning);
                Assert.Equal("outside-value", root.Body.GetValue("Outside:Value"));
                Assert.Equal("parent-value", root.Body.GetValue("Demo:Parent"));
                Assert.Equal("child-value", root.Body.GetValue("Demo:Parent:Child"));
            });

    [Fact]
    public Task I_configuration_identity_is_case_insensitive_across_boundaries() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(I_configuration_identity_is_case_insensitive_across_boundaries),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                ManagementCommandResponse receipt = await scenario.Management.SetAsync(
                    "UAT",
                    "demo:centralrequiredmessage",
                    "case-value");

                await scenario.StartDemoAndWaitUntilReadyAsync(
                    new DemoLaunchOptions(CentralEnvironment: "uat"));
                EffectiveConfigurationResponse effective =
                    await scenario.GetEffectiveConfigurationAsync();
                CurrentConfigurationResponse current = await scenario.Management.GetCurrentAsync(
                    "uAt",
                    "DEMO:CENTRALREQUIREDMESSAGE");

                Assert.Equal("case-value", effective.GetValue("CENTRALrequiredMESSAGE"));
                Assert.Equal(receipt.Environment, current.Environment);
                Assert.Equal(receipt.Key, current.Key);
                Assert.Equal(receipt.Version, current.Version);
                Assert.Equal("case-value", current.Value);
            },
            centralEnvironment: "UAT");
}
