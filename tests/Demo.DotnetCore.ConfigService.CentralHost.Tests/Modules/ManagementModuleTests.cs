using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Modules;

public sealed class ManagementModuleTests
{
    private static readonly EnvironmentName Environment = EnvironmentName.FromValidated("Production");
    private static readonly ConfigurationKey Key = ConfigurationKey.FromValidated("Demo:Message");
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 2, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task SetAsync_passes_one_utc_timestamp_to_one_atomic_store_call_and_returns_the_store_receipt()
    {
        var receipt = Receipt("StoredEnvironment", "Stored:Key", ConfigurationEntryState.Present);
        var store = new FakeConfigurationEntryStore { SetResult = receipt };
        var timeProvider = new ManualTimeProvider(Timestamp);
        var module = new ManagementModule(store, timeProvider);

        var result = await module.SetAsync(Environment, Key, "configured-value", CancellationToken.None);

        Assert.Same(receipt, result);
        var call = Assert.Single(store.SetRequests);
        Assert.Equal(Environment, call.Identity.Environment);
        Assert.Equal(Key, call.Identity.Key);
        Assert.Equal("configured-value", call.Value);
        Assert.Equal(Timestamp, call.CreatedAtUtc);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
        Assert.Empty(store.CurrentRequests);
        Assert.Empty(store.HistoryRequests);
        Assert.Empty(store.RemoveRequests);
        Assert.Empty(store.ResolveRequests);
    }

    [Fact]
    public async Task RemoveAsync_calls_only_the_atomic_remove_operation_with_one_utc_timestamp()
    {
        var receipt = Receipt("StoredEnvironment", "Stored:Key", ConfigurationEntryState.Removed);
        var store = new FakeConfigurationEntryStore
        {
            RemoveResult = new RemoveStoreResult.Created(receipt)
        };
        var timeProvider = new ManualTimeProvider(Timestamp);
        var module = new ManagementModule(store, timeProvider);

        var result = await module.RemoveAsync(Environment, Key, CancellationToken.None);

        Assert.Same(receipt, result);
        var call = Assert.Single(store.RemoveRequests);
        Assert.Equal(Environment, call.Identity.Environment);
        Assert.Equal(Key, call.Identity.Key);
        Assert.Equal(Timestamp, call.CreatedAtUtc);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
        Assert.Empty(store.CurrentRequests);
        Assert.Empty(store.HistoryRequests);
        Assert.Empty(store.SetRequests);
        Assert.Empty(store.ResolveRequests);
    }

    [Fact]
    public async Task RemoveAsync_maps_a_never_created_entry_to_the_safe_not_found_exception()
    {
        var store = new FakeConfigurationEntryStore { RemoveResult = new RemoveStoreResult.NotFound() };
        var module = new ManagementModule(store, new ManualTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<ConfigurationEntryNotFoundException>(() =>
            module.RemoveAsync(Environment, Key, CancellationToken.None));

        Assert.Equal("The requested Configuration Entry was not found.", exception.Message);
    }

    [Fact]
    public async Task RemoveAsync_maps_an_already_removed_entry_to_the_safe_already_removed_exception()
    {
        var store = new FakeConfigurationEntryStore { RemoveResult = new RemoveStoreResult.AlreadyRemoved() };
        var module = new ManagementModule(store, new ManualTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<ConfigurationEntryAlreadyRemovedException>(() =>
            module.RemoveAsync(Environment, Key, CancellationToken.None));

        Assert.Equal("The requested Configuration Entry is already removed.", exception.Message);
    }

    [Fact]
    public async Task GetCurrentAsync_maps_a_missing_record_to_the_safe_not_found_exception()
    {
        var store = new FakeConfigurationEntryStore();
        var module = new ManagementModule(store, new ManualTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<ConfigurationEntryNotFoundException>(() =>
            module.GetCurrentAsync(Environment, Key, CancellationToken.None));

        Assert.Equal("The requested Configuration Entry was not found.", exception.Message);
        Assert.Single(store.CurrentRequests);
    }

    [Fact]
    public async Task GetHistoryAsync_maps_a_missing_record_to_the_safe_not_found_exception()
    {
        var store = new FakeConfigurationEntryStore();
        var module = new ManagementModule(store, new ManualTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<ConfigurationEntryNotFoundException>(() =>
            module.GetHistoryAsync(Environment, Key, CancellationToken.None));

        Assert.Equal("The requested Configuration Entry was not found.", exception.Message);
        Assert.Single(store.HistoryRequests);
    }

    [Fact]
    public async Task GetCurrentAsync_and_GetHistoryAsync_return_stored_records_without_rewriting_display_spelling()
    {
        var storedEnvironment = EnvironmentName.FromValidated("StOrEdEnvironment");
        var storedKey = ConfigurationKey.FromValidated("Stored:Key");
        var current = new ConfigurationCurrent(
            storedEnvironment,
            storedKey,
            2,
            ConfigurationEntryState.Present,
            "stored-value",
            Timestamp);
        var history = new ConfigurationHistory(
            storedEnvironment,
            storedKey,
            [new ConfigurationVersion(2, ConfigurationOperation.Set, "stored-value", Timestamp)]);
        var store = new FakeConfigurationEntryStore { CurrentResult = current, HistoryResult = history };
        var module = new ManagementModule(store, new ManualTimeProvider(Timestamp));

        var returnedCurrent = await module.GetCurrentAsync(Environment, Key, CancellationToken.None);
        var returnedHistory = await module.GetHistoryAsync(Environment, Key, CancellationToken.None);

        Assert.Same(current, returnedCurrent);
        Assert.Same(history, returnedHistory);
        Assert.Equal("StOrEdEnvironment", returnedCurrent.Environment.Value);
        Assert.Equal("Stored:Key", returnedHistory.Key.Value);
    }

    private static ConfigurationCommandReceipt Receipt(
        string environment,
        string key,
        ConfigurationEntryState state) =>
        new(EnvironmentName.FromValidated(environment), ConfigurationKey.FromValidated(key), 7, state, Timestamp);
}
