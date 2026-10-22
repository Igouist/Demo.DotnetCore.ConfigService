using System.Security.Cryptography;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.CentralHost;

// POC-062: an opaque fingerprint of the requested keys' entry versions, computed per request and never stored.
internal static class ResolveRevision
{
    internal const string Prefix = "r1-";
    private const int HashBytes = 12;

    internal static string Compute(EnvironmentName environment, IReadOnlyList<ResolvedKeyVersion> keyVersions)
    {
        var hash = SHA256.HashData(CanonicalUtf8(environment, keyVersions));
        return Prefix + Convert.ToHexString(hash, 0, HashBytes).ToLowerInvariant();
    }

    // The canonical input is a JSON array, e.g. ["UAT",["DEMO:LOCALFALLBACK",0],["DEMO:MESSAGE",3]];
    // JSON escaping keeps keys that contain separators unambiguous. Upper-invariant folding matches the
    // ordinal case-insensitive identity of POC-028, so the spelling a consumer requests does not matter.
    internal static byte[] CanonicalUtf8(EnvironmentName environment, IReadOnlyList<ResolvedKeyVersion> keyVersions)
    {
        var canonicalKeys = keyVersions
            .Select(static keyVersion => (Key: keyVersion.Key.ToUpperInvariant(), keyVersion.Version))
            .OrderBy(static keyVersion => keyVersion.Key, StringComparer.Ordinal)
            .ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(environment.Value.ToUpperInvariant());
            foreach (var (key, version) in canonicalKeys)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(key);
                writer.WriteNumberValue(version);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }
}
