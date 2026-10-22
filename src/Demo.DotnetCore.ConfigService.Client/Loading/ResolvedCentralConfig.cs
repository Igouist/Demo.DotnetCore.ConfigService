namespace Demo.DotnetCore.ConfigService.Client;

internal sealed record ResolvedCentralConfig(
    IReadOnlyDictionary<string, string> Values,
    int MissingFallbackKeyCount,
    int AttemptCount,
    string? Revision = null);
