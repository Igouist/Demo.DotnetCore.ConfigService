namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed record SetConfigurationRequest(string? Environment, string? Key, string? Value);

internal sealed record RemoveConfigurationRequest(string? Environment, string? Key);

internal sealed record ConfigurationIdentityQuery(string? Environment, string? Key);

internal sealed record EnvironmentQuery(string? Environment);

internal sealed record ConfigurationCommandResponse(
    string Environment,
    string Key,
    long Version,
    ConfigurationEntryState State,
    DateTimeOffset CreatedAtUtc);

internal sealed record CurrentConfigurationResponse(
    string Environment,
    string Key,
    long Version,
    ConfigurationEntryState State,
    string? Value,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationVersionResponse(
    long Version,
    ConfigurationOperation Operation,
    string? Value,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationHistoryResponse(
    string Environment,
    string Key,
    IReadOnlyList<ConfigurationVersionResponse> Versions);

internal sealed record ConfigurationSummaryResponse(
    string Key,
    long Version,
    ConfigurationEntryState State,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationListResponse(
    string Environment,
    IReadOnlyList<ConfigurationSummaryResponse> Configurations);
