namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class ManagementModule
{
    private readonly IConfigurationEntryStore _store;
    private readonly TimeProvider _timeProvider;

    public ManagementModule(IConfigurationEntryStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public Task<ConfigurationCommandReceipt> SetAsync(
        EnvironmentName environment,
        ConfigurationKey key,
        string value,
        CancellationToken cancellationToken)
    {
        var identity = new ConfigurationEntryIdentity(environment, key);
        var createdAtUtc = _timeProvider.GetUtcNow();
        return _store.SetAsync(identity, value, createdAtUtc, cancellationToken);
    }

    public async Task<ConfigurationCommandReceipt> RemoveAsync(
        EnvironmentName environment,
        ConfigurationKey key,
        CancellationToken cancellationToken)
    {
        var identity = new ConfigurationEntryIdentity(environment, key);
        var createdAtUtc = _timeProvider.GetUtcNow();
        var result = await _store.RemoveAsync(identity, createdAtUtc, cancellationToken);

        return result switch
        {
            RemoveStoreResult.Created created => created.Receipt,
            RemoveStoreResult.NotFound => throw new ConfigurationEntryNotFoundException(),
            RemoveStoreResult.AlreadyRemoved => throw new ConfigurationEntryAlreadyRemovedException(),
            _ => throw new InvalidOperationException("Unknown remove store result.")
        };
    }

    public async Task<ConfigurationCurrent> GetCurrentAsync(
        EnvironmentName environment,
        ConfigurationKey key,
        CancellationToken cancellationToken)
    {
        var identity = new ConfigurationEntryIdentity(environment, key);
        return await _store.GetCurrentAsync(identity, cancellationToken)
            ?? throw new ConfigurationEntryNotFoundException();
    }

    public Task<IReadOnlyList<ConfigurationEntrySummary>> ListAsync(
        EnvironmentName environment,
        CancellationToken cancellationToken) =>
        _store.ListEntriesAsync(environment, cancellationToken);

    public async Task<ConfigurationHistory> GetHistoryAsync(
        EnvironmentName environment,
        ConfigurationKey key,
        CancellationToken cancellationToken)
    {
        var identity = new ConfigurationEntryIdentity(environment, key);
        return await _store.GetHistoryAsync(identity, cancellationToken)
            ?? throw new ConfigurationEntryNotFoundException();
    }
}
