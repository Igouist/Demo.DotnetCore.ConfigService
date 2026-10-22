using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Modules;

public sealed class DistributionModuleTests
{
    private static readonly EnvironmentName Environment = EnvironmentName.FromValidated("Production");

    [Fact]
    public async Task ResolveAsync_partitions_requested_keys_using_stored_spelling_and_preserves_missing_request_order()
    {
        var requestedKeys = new[]
        {
            ConfigurationKey.FromValidated("Request:Present"),
            ConfigurationKey.FromValidated("Request:Removed"),
            ConfigurationKey.FromValidated("Request:NeverCreated"),
            ConfigurationKey.FromValidated("Request:Empty")
        };
        var store = new FakeConfigurationEntryStore
        {
            ResolutionResults =
            [
                new StoredResolution(
                    ConfigurationKey.FromValidated("rEqUeSt:pReSeNt"),
                    3,
                    ConfigurationEntryState.Present,
                    "configured-value"),
                new StoredResolution(
                    ConfigurationKey.FromValidated("REQUEST:removed"),
                    2,
                    ConfigurationEntryState.Removed,
                    null),
                new StoredResolution(
                    ConfigurationKey.FromValidated("request:EMPTY"),
                    1,
                    ConfigurationEntryState.Present,
                    string.Empty)
            ]
        };
        var module = new DistributionModule(store);

        var resolution = await module.ResolveAsync(Environment, requestedKeys, CancellationToken.None);

        Assert.Equal(2, resolution.Values.Count);
        Assert.Equal("configured-value", resolution.Values["rEqUeSt:pReSeNt"]);
        Assert.Equal(string.Empty, resolution.Values["request:EMPTY"]);
        Assert.Equal(["REQUEST:removed", "Request:NeverCreated"], resolution.MissingKeys);
        Assert.DoesNotContain("Request:Present", resolution.Values.Keys);
        Assert.DoesNotContain("Request:Empty", resolution.Values.Keys);
        Assert.DoesNotContain("Request:Removed", resolution.MissingKeys);
        Assert.Equal(
            requestedKeys.Select(static key => key.Value).OrderBy(static key => key, StringComparer.OrdinalIgnoreCase),
            resolution.Values.Keys.Concat(resolution.MissingKeys).OrderBy(static key => key, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            ["rEqUeSt:pReSeNt=3", "REQUEST:removed=2", "Request:NeverCreated=0", "request:EMPTY=1"],
            resolution.KeyVersions.Select(static keyVersion => $"{keyVersion.Key}={keyVersion.Version}"));
        Assert.Equal(ResolveRevision.Compute(Environment, resolution.KeyVersions), resolution.Revision);
        var storeCall = Assert.Single(store.ResolveRequests);
        Assert.Equal(Environment, storeCall.Environment);
        Assert.Same(requestedKeys, storeCall.Keys);
        Assert.Empty(store.CurrentRequests);
        Assert.Empty(store.HistoryRequests);
        Assert.Empty(store.SetRequests);
        Assert.Empty(store.RemoveRequests);
    }

    [Fact]
    public Task ResolveAsync_rejects_a_present_entry_with_a_null_value() =>
        AssertInvariantViolationAsync(ConfigurationEntryState.Present, null);

    [Fact]
    public Task ResolveAsync_rejects_a_removed_entry_with_a_non_null_value() =>
        AssertInvariantViolationAsync(ConfigurationEntryState.Removed, "unexpected-value");

    [Fact]
    public Task ResolveAsync_rejects_an_entry_without_a_positive_version() =>
        AssertInvariantViolationAsync(ConfigurationEntryState.Present, "value", version: 0);

    private static async Task AssertInvariantViolationAsync(
        ConfigurationEntryState state,
        string? value,
        long version = 1)
    {
        var requestedKey = ConfigurationKey.FromValidated("Request:Key");
        var store = new FakeConfigurationEntryStore
        {
            ResolutionResults = [new StoredResolution(requestedKey, version, state, value)]
        };
        var module = new DistributionModule(store);

        var exception = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            module.ResolveAsync(Environment, [requestedKey], CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, exception.Category);
        var inner = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal("Stored resolution violates its state/value invariant.", inner.Message);
    }
}
