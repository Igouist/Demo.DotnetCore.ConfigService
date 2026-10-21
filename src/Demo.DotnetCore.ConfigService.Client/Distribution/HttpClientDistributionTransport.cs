using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class HttpClientDistributionTransport : IDistributionTransport
{
    private readonly HttpClient _httpClient;

    internal HttpClientDistributionTransport(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                "The Client transport requires package-owned timeout tokens.",
                nameof(httpClient));
        }

        _httpClient = httpClient;
    }

    public async Task<DistributionHttpResponse> ResolveAsync(
        Uri endpoint,
        DistributionResolveRequest request,
        CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return new DistributionHttpResponse(
                response.StatusCode,
                await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        return new DistributionHttpResponse(
            response.StatusCode,
            ReadOnlyMemory<byte>.Empty,
            await TryReadProblemAsync(response, cancellationToken));
    }

    // Non-200 bodies are read only as Central Problem Details, and only their validated code and trace id
    // leave this adapter; a failed read keeps the HTTP status as the whole result.
    private static async Task<CentralProblemReference?> TryReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                CentralProblemReference.MediaType,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return CentralProblemReference.Read(body);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
