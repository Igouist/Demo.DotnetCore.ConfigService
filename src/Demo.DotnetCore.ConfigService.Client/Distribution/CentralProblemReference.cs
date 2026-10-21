using System.Text.Json;
using System.Text.RegularExpressions;

namespace Demo.DotnetCore.ConfigService.Client;

// The only facts taken from a non-200 Problem Details body: the stable error code and the trace id that
// locates the Central log entry. POC-039 forbids raw Central text, so each field must match a strict format.
internal sealed record CentralProblemReference(string? Code, string? TraceId)
{
    internal const string MediaType = "application/problem+json";

    internal static CentralProblemReference? Read(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? code = ReadMatching(document.RootElement, "code", CentralProblemFormats.Code());
            string? traceId = ReadMatching(document.RootElement, "traceId", CentralProblemFormats.TraceId());
            return code is null && traceId is null ? null : new CentralProblemReference(code, traceId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadMatching(JsonElement root, string name, Regex format) =>
        root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } value
            && format.IsMatch(value)
                ? value
                : null;
}

internal static partial class CentralProblemFormats
{
    // Central codes are snake_case identifiers such as persistence_failure. \z, unlike $, rejects a trailing newline.
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex Code();

    // W3C traceparent (Activity.Id) or the ASP.NET Core TraceIdentifier fallback such as 0HN1ABC:00000001.
    [GeneratedRegex(
        @"^([0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}|[0-9A-Z]{1,32}:[0-9A-F]{8})\z",
        RegexOptions.CultureInvariant)]
    internal static partial Regex TraceId();
}
