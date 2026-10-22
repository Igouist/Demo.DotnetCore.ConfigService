namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record SetConfigurationRequest(string Environment, string Key, string Value, string ChangedBy);

internal sealed record RemoveConfigurationRequest(string Environment, string Key, string ChangedBy);

internal sealed record ManagementCommandResponse(
    string Environment,
    string Key,
    long Version,
    string State,
    DateTimeOffset CreatedAtUtc);

internal sealed record CurrentConfigurationResponse(
    string Environment,
    string Key,
    long Version,
    string State,
    string? Value,
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationVersionResponse(
    long Version,
    string Operation,
    string? Value,
    DateTimeOffset CreatedAtUtc,
    string ChangedBy,
    string? Reason);

internal sealed record ConfigurationHistoryResponse(
    string Environment,
    string Key,
    IReadOnlyList<ConfigurationVersionResponse> Versions);

internal sealed record CentralProblemResponse(string Code);
