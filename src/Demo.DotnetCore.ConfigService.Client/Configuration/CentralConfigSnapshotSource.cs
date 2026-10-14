using System.Collections.ObjectModel;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class CentralConfigSnapshotSource : IConfigurationSource
{
    private readonly IDictionary<string, string?> _values;

    internal CentralConfigSnapshotSource(IReadOnlyDictionary<string, string> values)
    {
        var preparedValues = new Dictionary<string, string?>(
            values.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in values)
        {
            preparedValues.Add(pair.Key, pair.Value);
        }

        _values = new ReadOnlyDictionary<string, string?>(preparedValues);
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new CentralConfigSnapshotProvider(_values);
}
