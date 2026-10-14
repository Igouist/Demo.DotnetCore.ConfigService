using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class StrictJsonRequestReader
{
    private readonly JsonSerializerOptions _serializerOptions;

    public StrictJsonRequestReader(IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions) =>
        _serializerOptions = jsonOptions.Value.JsonSerializerOptions;

    public async Task<TRequest> ReadRequiredAsync<TRequest>(
        HttpRequest request,
        IReadOnlySet<string> allowedTopLevelMembers,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length == 0)
        {
            throw RequestValidationException.Single(
                "$", "body_required", "A JSON request body is required.");
        }

        buffer.Position = 0;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw RequestValidationException.Single(
                "$", "invalid_json", "The request body must contain valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw RequestValidationException.Single(
                    "$", "invalid_type", "A request value has an incompatible JSON type.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowedTopLevelMembers.Contains(property.Name))
                {
                    throw RequestValidationException.Single(
                        "$", "unknown_property", "The request body contains an unknown property.");
                }
            }

            try
            {
                return document.RootElement.Deserialize<TRequest>(_serializerOptions)
                    ?? throw RequestValidationException.Single(
                        "$", "invalid_type", "A request value has an incompatible JSON type.");
            }
            catch (JsonException exception)
            {
                throw RequestValidationException.Single(
                    string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path,
                    "invalid_type",
                    "A request value has an incompatible JSON type.");
            }
        }
    }
}
