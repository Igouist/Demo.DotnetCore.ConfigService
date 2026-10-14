namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

internal sealed class FakeConfigurationEntryStore : IConfigurationEntryStore
{
    public ConfigurationCurrent? CurrentResult { get; set; }

    public ConfigurationHistory? HistoryResult { get; set; }

    public IReadOnlyList<StoredResolution> ResolutionResults { get; set; } = [];

    public ConfigurationCommandReceipt? SetResult { get; set; }

    public RemoveStoreResult? RemoveResult { get; set; }

    public List<ConfigurationEntryIdentity> CurrentRequests { get; } = [];

    public List<ConfigurationEntryIdentity> HistoryRequests { get; } = [];

    public List<(EnvironmentName Environment, IReadOnlyList<ConfigurationKey> Keys)> ResolveRequests { get; } = [];

    public List<(ConfigurationEntryIdentity Identity, string Value, DateTimeOffset CreatedAtUtc)> SetRequests { get; } = [];

    public List<(ConfigurationEntryIdentity Identity, DateTimeOffset CreatedAtUtc)> RemoveRequests { get; } = [];

    public Task<ConfigurationCurrent?> GetCurrentAsync(
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken)
    {
        CurrentRequests.Add(identity);
        return Task.FromResult(CurrentResult);
    }

    public Task<ConfigurationHistory?> GetHistoryAsync(
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken)
    {
        HistoryRequests.Add(identity);
        return Task.FromResult(HistoryResult);
    }

    public Task<IReadOnlyList<StoredResolution>> ResolveExistingAsync(
        EnvironmentName environment,
        IReadOnlyList<ConfigurationKey> keys,
        CancellationToken cancellationToken)
    {
        ResolveRequests.Add((environment, keys));
        return Task.FromResult(ResolutionResults);
    }

    public Task<ConfigurationCommandReceipt> SetAsync(
        ConfigurationEntryIdentity identity,
        string value,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        SetRequests.Add((identity, value, createdAtUtc));
        return Task.FromResult(SetResult ?? throw new InvalidOperationException("A Set result must be configured."));
    }

    public Task<RemoveStoreResult> RemoveAsync(
        ConfigurationEntryIdentity identity,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        RemoveRequests.Add((identity, createdAtUtc));
        return Task.FromResult(RemoveResult ?? throw new InvalidOperationException("A Remove result must be configured."));
    }
}
