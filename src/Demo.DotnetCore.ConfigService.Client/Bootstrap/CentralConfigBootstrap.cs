namespace Demo.DotnetCore.ConfigService.Client;

internal sealed record EnabledCentralConfigBootstrap(
    string Environment,
    Uri ResolveEndpoint,
    KeySelection KeySelection);

internal readonly record struct BootstrapReadResult(
    bool IsValid,
    bool IsEnabled,
    EnabledCentralConfigBootstrap? EnabledBootstrap)
{
    internal static BootstrapReadResult Invalid => new(false, false, null);

    internal static BootstrapReadResult Disabled => new(true, false, null);

    internal static BootstrapReadResult Enabled(EnabledCentralConfigBootstrap bootstrap) =>
        new(true, true, bootstrap);
}
