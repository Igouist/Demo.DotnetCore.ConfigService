using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal enum CentralReadinessObservation
{
    Ready,
    Unavailable,
    ForeignInstance
}

internal sealed class CentralReadinessContractException : InvalidOperationException
{
    public CentralReadinessContractException()
        : base("Central readiness response contract is invalid.")
    {
    }
}

internal static class CentralReadinessResponseReader
{
    internal static async Task<CentralReadinessObservation> ReadAsync(
        HttpResponseMessage response,
        string expectedInstanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedInstanceId);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable)
            || !HasSingleJsonContentType(response)
            || !HasExactNoStoreHeader(response))
        {
            throw new CentralReadinessContractException();
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new CentralReadinessContractException();
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string[] members = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject()
                    .Select(static property => property.Name)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToArray()
                : [];
            if (!members.SequenceEqual(
                    ["instanceId", "status"],
                    StringComparer.Ordinal)
                || !root.TryGetProperty("status", out JsonElement statusElement)
                || statusElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("instanceId", out JsonElement instanceElement)
                || instanceElement.ValueKind != JsonValueKind.String)
            {
                throw new CentralReadinessContractException();
            }

            string status = statusElement.GetString()!;
            string instanceId = instanceElement.GetString()!;
            bool statusMatches = response.StatusCode == HttpStatusCode.OK
                ? StringComparer.Ordinal.Equals(status, "ready")
                : StringComparer.Ordinal.Equals(status, "unavailable");
            if (!statusMatches
                || !Guid.TryParseExact(instanceId, "N", out Guid parsed)
                || !StringComparer.Ordinal.Equals(parsed.ToString("N"), instanceId))
            {
                throw new CentralReadinessContractException();
            }

            if (!StringComparer.Ordinal.Equals(instanceId, expectedInstanceId))
            {
                return CentralReadinessObservation.ForeignInstance;
            }

            return response.StatusCode == HttpStatusCode.OK
                ? CentralReadinessObservation.Ready
                : CentralReadinessObservation.Unavailable;
        }
    }

    private static bool HasSingleJsonContentType(HttpResponseMessage response) =>
        response.Content.Headers.NonValidated.TryGetValues("Content-Type", out HeaderStringValues values)
        && values.Count == 1
        && MediaTypeHeaderValue.TryParse(values.Single(), out MediaTypeHeaderValue? contentType)
        && StringComparer.OrdinalIgnoreCase.Equals(contentType.MediaType, "application/json");

    private static bool HasExactNoStoreHeader(HttpResponseMessage response) =>
        response.Headers.NonValidated.TryGetValues("Cache-Control", out HeaderStringValues values)
        && values.Count == 1
        && StringComparer.OrdinalIgnoreCase.Equals(values.Single(), "no-store");
}
