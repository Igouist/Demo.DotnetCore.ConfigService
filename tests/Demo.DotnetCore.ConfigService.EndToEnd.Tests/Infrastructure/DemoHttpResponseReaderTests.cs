using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class DemoHttpResponseReaderTests
{
    private const string Route = "/api/v1/demo/unsafe/root-configuration";

    [Fact]
    public async Task Reader_returns_status_media_type_no_store_and_the_wire_contract()
    {
        using HttpResponseMessage response = JsonResponse(
            """
            {
              "scope": "Root",
              "warning": "unsafe warning",
              "effectiveValues": { "Outside:Value": "outside-value" }
            }
            """);
        response.Headers.CacheControl = CacheControlHeaderValue.Parse("private, no-store");

        DemoHttpResult<UnsafeRootConfigurationResponse> result =
            await DemoHttpResponseReader.ReadAsync<UnsafeRootConfigurationResponse>(
                response,
                Route);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("application/json", result.MediaType);
        Assert.True(result.HasNoStore);
        Assert.Equal("Root", result.Body.Scope);
        Assert.Equal("outside-value", result.Body.GetValue("outside:value"));
    }

    [Fact]
    public async Task Reader_rejects_non_success_without_exposing_the_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("unsafe-body-secret")
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DemoHttpResponseReader.ReadAsync<UnsafeRootConfigurationResponse>(
                response,
                Route));

        AssertSafeFailure(exception, $"GET {Route} returned status 503.");
    }

    [Fact]
    public async Task Reader_rejects_the_wrong_media_type_without_exposing_the_body()
    {
        using HttpResponseMessage response = JsonResponse("unsafe-body-secret", "text/plain");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DemoHttpResponseReader.ReadAsync<UnsafeRootConfigurationResponse>(
                response,
                Route));

        AssertSafeFailure(
            exception,
            $"GET {Route} returned status 200 with an invalid media type.");
    }

    [Fact]
    public async Task Reader_rejects_a_null_contract_without_exposing_the_body()
    {
        using HttpResponseMessage response = JsonResponse("null");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DemoHttpResponseReader.ReadAsync<UnsafeRootConfigurationResponse>(
                response,
                Route));

        AssertSafeFailure(
            exception,
            $"GET {Route} returned status 200 without a response contract.");
    }

    [Fact]
    public async Task Reader_rejects_the_wrong_contract_type_without_exposing_the_body()
    {
        using HttpResponseMessage response = JsonResponse(
            """
            { "scope": ["unsafe-body-secret"] }
            """);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DemoHttpResponseReader.ReadAsync<UnsafeRootConfigurationResponse>(
                response,
                Route));

        AssertSafeFailure(
            exception,
            $"GET {Route} returned status 200 with an invalid response contract.");
    }

    [Fact]
    public async Task Reader_hides_unsafe_body_read_failure_details()
    {
        const string unsafeBodyReadFailureSecret = "unsafe-body-read-failure-secret";
        using HttpResponseMessage response = StreamResponse(
            new FailingReadStream(() => new IOException(unsafeBodyReadFailureSecret)));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DemoHttpResponseReader.ReadAsync<UnsafeRootConfigurationResponse>(
                response,
                Route));

        AssertSafeFailure(exception, $"GET {Route} failed.");
        Assert.DoesNotContain(unsafeBodyReadFailureSecret, exception.ToString(), StringComparison.Ordinal);
    }

    private static void AssertSafeFailure(
        InvalidOperationException exception,
        string expectedMessage)
    {
        Assert.Equal(expectedMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("unsafe-body-secret", exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(
        string content,
        string mediaType = "application/json") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FailingReadStream(Func<Exception> createException) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw createException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => Task.FromException<int>(createException());

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(createException());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
