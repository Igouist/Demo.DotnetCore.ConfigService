using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json.Serialization;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed record DistributionResolveRequest(
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("keys")] IReadOnlyList<string> Keys);

internal sealed record DistributionHttpResponse(
    HttpStatusCode StatusCode,
    ReadOnlyMemory<byte> Body,
    CentralProblemReference? Problem = null);

internal sealed class ValidatedDistributionResponse
{
    private readonly HashSet<string> _missingKeys;

    internal ValidatedDistributionResponse(
        IDictionary<string, string> values,
        IEnumerable<string> missingKeys,
        IReadOnlyList<string>? unknownMembers = null)
    {
        Values = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
        _missingKeys = new HashSet<string>(missingKeys, StringComparer.OrdinalIgnoreCase);
        UnknownMembers = unknownMembers?.ToArray() ?? [];
    }

    internal IReadOnlyDictionary<string, string> Values { get; }

    internal IReadOnlyList<string> UnknownMembers { get; }

    internal int MissingKeyCount => _missingKeys.Count;

    internal bool IsMissing(string key) => _missingKeys.Contains(key);
}
