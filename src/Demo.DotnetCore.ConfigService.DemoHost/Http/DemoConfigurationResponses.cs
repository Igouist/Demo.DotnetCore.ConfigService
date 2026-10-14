namespace Demo.DotnetCore.ConfigService.DemoHost;

public sealed record EffectiveConfigurationResponse(
    string Scope,
    IReadOnlyDictionary<string, string> EffectiveValues);

public sealed record UnsafeRootConfigurationResponse(
    string Scope,
    string Warning,
    IReadOnlyDictionary<string, string> EffectiveValues);
