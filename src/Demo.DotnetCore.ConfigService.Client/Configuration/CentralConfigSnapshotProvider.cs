using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.Client;

// The snapshot never reloads from Central. IConfiguration[key] writes (WebHost.UseUrls, UseSetting) call Set on
// every provider, so Set keeps the default in-memory behavior instead of failing the consumer's startup.
internal sealed class CentralConfigSnapshotProvider(
    IDictionary<string, string?> preparedValues) : ConfigurationProvider
{
    public override void Load() => Data = preparedValues;
}
