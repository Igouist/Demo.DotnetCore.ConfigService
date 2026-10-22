namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed record ResolveConfigurationsRequest(string? Environment, string?[]? Keys);

internal sealed record ResolveConfigurationsResponse(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> MissingKeys,
    string Revision);
