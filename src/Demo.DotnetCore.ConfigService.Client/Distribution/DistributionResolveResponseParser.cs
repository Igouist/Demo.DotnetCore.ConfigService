using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.Client;

internal interface IDistributionResponseParser
{
    ValidatedDistributionResponse? ParseAndValidate(
        ReadOnlyMemory<byte> utf8Json,
        KeySelection requestedSelection);
}

internal sealed class DistributionResponseParser : IDistributionResponseParser
{
    internal static DistributionResponseParser Instance { get; } = new();

    private DistributionResponseParser()
    {
    }

    public ValidatedDistributionResponse? ParseAndValidate(
        ReadOnlyMemory<byte> utf8Json,
        KeySelection requestedSelection) =>
        DistributionResolveResponseParser.TryParseAndValidate(
            utf8Json,
            requestedSelection,
            out ValidatedDistributionResponse? response)
            ? response
            : null;
}

internal static class DistributionResolveResponseParser
{
    internal const int MaxRevisionLength = 128;

    internal static bool TryParseAndValidate(
        ReadOnlyMemory<byte> utf8Json,
        KeySelection requestedSelection,
        [NotNullWhen(true)]
        out ValidatedDistributionResponse? response)
    {
        response = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryFindRequiredMembers(
                    document.RootElement,
                    out JsonElement valuesElement,
                    out JsonElement missingElement,
                    out JsonElement? revisionElement,
                    out IReadOnlyList<string> unknownMembers)
                || !TryReadValues(valuesElement, out Dictionary<string, string> values)
                || !TryReadMissingKeys(missingElement, out HashSet<string> missingKeys)
                || !TryReadRevision(revisionElement, out string? revision)
                || values.Keys.Any(key => !requestedSelection.Contains(key))
                || missingKeys.Any(key => !requestedSelection.Contains(key)))
            {
                return false;
            }

            foreach (SelectedConfigurationKey requested in requestedSelection.Keys)
            {
                bool hasValue = values.ContainsKey(requested.DeclaredKey);
                bool isMissing = missingKeys.Contains(requested.DeclaredKey);
                if (hasValue == isMissing)
                {
                    return false;
                }
            }

            response = new ValidatedDistributionResponse(values, missingKeys, unknownMembers, revision);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindRequiredMembers(
        JsonElement root,
        out JsonElement values,
        out JsonElement missingKeys,
        out JsonElement? revision,
        out IReadOnlyList<string> unknownMembers)
    {
        values = default;
        missingKeys = default;
        revision = null;
        var unknown = new List<string>();
        unknownMembers = unknown;
        bool sawValues = false;
        bool sawMissingKeys = false;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "values", StringComparison.OrdinalIgnoreCase))
            {
                if (sawValues)
                {
                    return false;
                }

                sawValues = true;
                values = property.Value;
            }
            else if (string.Equals(property.Name, "missingKeys", StringComparison.OrdinalIgnoreCase))
            {
                if (sawMissingKeys)
                {
                    return false;
                }

                sawMissingKeys = true;
                missingKeys = property.Value;
            }
            else if (string.Equals(property.Name, "revision", StringComparison.OrdinalIgnoreCase))
            {
                if (revision is not null)
                {
                    return false;
                }

                revision = property.Value;
            }
            else if (!unknown.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                // ADR-0003: Central may add top-level response members; skip them and report their names.
                unknown.Add(property.Name);
            }
        }

        return sawValues && sawMissingKeys;
    }

    // POC-062: an older Central omits the revision; when present it is an opaque, log-safe string.
    private static bool TryReadRevision(JsonElement? element, out string? revision)
    {
        revision = null;
        if (element is null)
        {
            return true;
        }

        if (element.Value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string candidate = element.Value.GetString()!;
        if (candidate.Length is 0 or > MaxRevisionLength
            || candidate.Any(static character => character is < '!' or > '~'))
        {
            return false;
        }

        revision = candidate;
        return true;
    }

    private static bool TryReadValues(
        JsonElement element,
        out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!ConfigurationKeyRules.IsValid(property.Name)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string value = property.Value.GetString()!;
            if (!ConfigurationValueLimits.IsValid(value)
                || !values.TryAdd(property.Name, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadMissingKeys(
        JsonElement element,
        out HashSet<string> missingKeys)
    {
        missingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string key = item.GetString()!;
            if (!ConfigurationKeyRules.IsValid(key) || !missingKeys.Add(key))
            {
                return false;
            }
        }

        return true;
    }
}
