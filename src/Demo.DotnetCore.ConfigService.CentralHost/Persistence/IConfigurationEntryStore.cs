namespace Demo.DotnetCore.ConfigService.CentralHost;

internal interface IConfigurationEntryStore
{
    Task<ConfigurationCurrent?> GetCurrentAsync(
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken);

    Task<ConfigurationHistory?> GetHistoryAsync(
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigurationEntrySummary>> ListEntriesAsync(
        EnvironmentName environment,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredResolution>> ResolveExistingAsync(
        EnvironmentName environment,
        IReadOnlyList<ConfigurationKey> keys,
        CancellationToken cancellationToken);

    Task<ConfigurationCommandReceipt> SetAsync(
        ConfigurationEntryIdentity identity,
        string value,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);

    Task<RemoveStoreResult> RemoveAsync(
        ConfigurationEntryIdentity identity,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);
}
