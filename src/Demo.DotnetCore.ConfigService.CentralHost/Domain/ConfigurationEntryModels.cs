namespace Demo.DotnetCore.ConfigService.CentralHost;

internal enum ConfigurationEntryState
{
    Present,
    Removed
}

internal enum ConfigurationOperation
{
    Set,
    Remove
}

internal readonly record struct ConfigurationEntryIdentity(
    EnvironmentName Environment,
    ConfigurationKey Key);

internal sealed record ConfigurationCommandReceipt(
    EnvironmentName Environment,
    ConfigurationKey Key,
    long Version,
    ConfigurationEntryState State,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationCurrent(
    EnvironmentName Environment,
    ConfigurationKey Key,
    long Version,
    ConfigurationEntryState State,
    string? Value,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationVersion(
    long Version,
    ConfigurationOperation Operation,
    string? Value,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationHistory(
    EnvironmentName Environment,
    ConfigurationKey Key,
    IReadOnlyList<ConfigurationVersion> Versions);

internal sealed record ConfigurationEntrySummary(
    ConfigurationKey Key,
    long Version,
    ConfigurationEntryState State,
    DateTimeOffset CreatedAtUtc);

internal sealed record StoredResolution(
    ConfigurationKey Key,
    ConfigurationEntryState State,
    string? Value);

internal sealed record ConfigurationResolution(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> MissingKeys);
