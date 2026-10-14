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

        ReadOnlyMemory<byte> responseBody = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadAsByteArrayAsync(cancellationToken)
            : ReadOnlyMemory<byte>.Empty;
        return new DistributionHttpResponse(response.StatusCode, responseBody);
    }
}
