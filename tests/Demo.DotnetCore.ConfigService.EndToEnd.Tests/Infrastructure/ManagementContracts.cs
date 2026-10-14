namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record SetConfigurationRequest(string Environment, string Key, string Value);

internal sealed record RemoveConfigurationRequest(string Environment, string Key);

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
    DateTimeOffset CreatedAtUtc);

internal sealed record ConfigurationHistoryResponse(
    string Environment,
    string Key,
    IReadOnlyList<ConfigurationVersionResponse> Versions);

internal sealed record CentralProblemResponse(string Code);
