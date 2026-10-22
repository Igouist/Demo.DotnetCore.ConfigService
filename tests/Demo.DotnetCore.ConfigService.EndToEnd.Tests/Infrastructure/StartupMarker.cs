using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record StartupMarker(
    string Event,
    int? ValueCount,
    int? MissingFallbackKeyCount,
    int? AttemptCount,
    string? FailureKind,
    int? HttpStatusCode,
    double? DelayMs,
    double? ElapsedMs,
    string[]? MissingRequiredKeys = null,
    string? Revision = null)
{
    private const string ExpectedSource = "demo-central-config";

    internal static bool TryParse(string line, out StartupMarker? marker)
    {
        marker = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadString(root, "source", out string? source)
                || !string.Equals(source, ExpectedSource, StringComparison.Ordinal)
                || !TryReadString(root, "event", out string? eventName))
            {
                return false;
            }

            marker = new StartupMarker(
                eventName!,
                ReadInt32(root, "valueCount"),
                ReadInt32(root, "missingFallbackKeyCount"),
                ReadInt32(root, "attemptCount"),
                ReadString(root, "failureKind"),
                ReadInt32(root, "httpStatusCode"),
                ReadDouble(root, "delayMs"),
                ReadDouble(root, "elapsedMs"),
                ReadStringArray(root, "missingRequiredKeys"),
                ReadString(root, "revision"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadString(
        JsonElement root,
        string name,
        out string? value)
    {
        value = null;
        return root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        TryReadString(root, name, out string? value) ? value : null;

    private static string[]? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array
            || property.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            return null;
        }

        return property.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static int? ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int value)
                ? value
                : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out double value)
                ? value
                : null;
}
