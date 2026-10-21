using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class CentralConfigSnapshotSource : IConfigurationSource
{
    private readonly Dictionary<string, string?> _values;

    internal CentralConfigSnapshotSource(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string?>(
            values.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in values)
        {
            _values.Add(pair.Key, pair.Value);
        }
    }

    // Each provider gets its own copy so in-memory writes to one built provider never leak into the source.
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new CentralConfigSnapshotProvider(
            new Dictionary<string, string?>(_values, StringComparer.OrdinalIgnoreCase));
}
