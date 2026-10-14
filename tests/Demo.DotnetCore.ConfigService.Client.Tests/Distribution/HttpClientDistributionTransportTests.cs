using System.Net;
using System.Text;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Distribution;

public sealed class HttpClientDistributionTransportTests
{
    [Fact]
    public async Task Sends_one_post_with_the_local_wire_shape_and_returns_200_body()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"values\":{},\"missingKeys\":[]}");
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientDistributionTransport(client);

        DistributionHttpResponse result = await transport.ResolveAsync(
            new Uri("https://central/prefix/api/v1/distribution/configurations/resolve"),
            new DistributionResolveRequest("UAT", ["Demo:A", "Demo:B"]),
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://central/prefix/api/v1/distribution/configurations/resolve", handler.Uri!.AbsoluteUri);
        Assert.Equal("application/json", handler.MediaType);
        using JsonDocument requestBody = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("UAT", requestBody.RootElement.GetProperty("environment").GetString());
        Assert.Equal(
            ["Demo:A", "Demo:B"],
            requestBody.RootElement.GetProperty("keys").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("{\"values\":{},\"missingKeys\":[]}", Encoding.UTF8.GetString(result.Body.Span));
    }

    [Fact]
    public async Task Non_200_response_body_is_never_consumed()
    {
        var handler = new ThrowingContentHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientDistributionTransport(client);

        DistributionHttpResponse result = await transport.ResolveAsync(
            new Uri("https://central/api/v1/distribution/configurations/resolve"),
            new DistributionResolveRequest("UAT", ["Demo:A"]),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.True(result.Body.IsEmpty);
    }

    [Fact]
    public async Task Caller_token_covers_the_200_response_body_read()
    {
        var handler = new BlockingContentHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientDistributionTransport(client);
        using var cancellation = new CancellationTokenSource();

        Task<DistributionHttpResponse> load = transport.ResolveAsync(
            new Uri("https://central/api/v1/distribution/configurations/resolve"),
            new DistributionResolveRequest("UAT", ["Demo:A"]),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        internal HttpMethod? Method { get; private set; }
        internal Uri? Uri { get; private set; }
        internal string? MediaType { get; private set; }
        internal string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingContentHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode) { Content = new ThrowingContent() });
    }

    private sealed class BlockingContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() });
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The body must not be read.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
