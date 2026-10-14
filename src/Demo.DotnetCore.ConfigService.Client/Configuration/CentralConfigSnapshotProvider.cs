using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class CentralConfigSnapshotProvider(
    IDictionary<string, string?> preparedValues) : ConfigurationProvider
{
    public override void Load() => Data = preparedValues;

    public override void Set(string key, string? value) =>
        throw new NotSupportedException("The central configuration startup snapshot is immutable.");
}
