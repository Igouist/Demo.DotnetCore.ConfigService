namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class DistributionModule
{
    private readonly IConfigurationEntryStore _store;

    public DistributionModule(IConfigurationEntryStore store) => _store = store;

    public async Task<ConfigurationResolution> ResolveAsync(
        EnvironmentName environment,
        IReadOnlyList<ConfigurationKey> keys,
        CancellationToken cancellationToken)
    {
        var storedResolutions = await _store.ResolveExistingAsync(environment, keys, cancellationToken);
        var resolutionsByKey = new Dictionary<string, StoredResolution>(StringComparer.OrdinalIgnoreCase);

        foreach (var storedResolution in storedResolutions)
        {
            ValidateStateValueInvariant(storedResolution);
            resolutionsByKey.Add(storedResolution.Key.Value, storedResolution);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missingKeys = new List<string>();

        foreach (var key in keys)
        {
            if (!resolutionsByKey.TryGetValue(key.Value, out var storedResolution))
            {
                missingKeys.Add(key.Value);
                continue;
            }

            if (storedResolution.State == ConfigurationEntryState.Present)
            {
                values.Add(storedResolution.Key.Value, storedResolution.Value!);
            }
            else
            {
                missingKeys.Add(storedResolution.Key.Value);
            }
        }

        return new ConfigurationResolution(values, missingKeys);
    }

    private static void ValidateStateValueInvariant(StoredResolution storedResolution)
    {
        if ((storedResolution.State == ConfigurationEntryState.Present && storedResolution.Value is null) ||
            (storedResolution.State == ConfigurationEntryState.Removed && storedResolution.Value is not null) ||
            (storedResolution.State is not ConfigurationEntryState.Present and not ConfigurationEntryState.Removed))
        {
            throw new PersistenceFailureException(
                PersistenceFailureCategory.Other,
                new InvalidDataException("Stored resolution violates its state/value invariant."));
        }
    }
}
