using System.Net;
using System.Text;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class ManagementClientTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:43210/");

    [Fact]
    public async Task Current_query_escapes_the_identity_and_reads_the_wire_contract()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "environment": "UAT & Blue",
              "key": "Demo:A/B?C",
              "version": 7,
              "state": "Present",
              "value": "current-value",
              "createdAtUtc": "2026-09-05T01:02:03+00:00"
            }
            """));
        using var client = new ManagementClient(BaseAddress, handler);

        CurrentConfigurationResponse response = await client.GetCurrentAsync(
            "UAT & Blue",
            "Demo:A/B?C");

        Assert.Equal(
            "/api/v1/management/configurations/current?environment=UAT%20%26%20Blue&key=Demo%3AA%2FB%3FC",
            handler.RequestPathAndQuery);
        Assert.Equal([ManagementClient.ApiKey], handler.ApiKeyHeaderValues);
        Assert.Equal(("UAT & Blue", "Demo:A/B?C", 7L, "Present", "current-value"),
            (response.Environment, response.Key, response.Version, response.State, response.Value));
    }

    [Fact]
    public async Task History_query_escapes_the_identity_and_reads_newest_first_versions()
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "environment": "UAT + Green",
              "key": "Demo:Message #1",
              "versions": [
                {
                  "version": 2,
                  "operation": "Remove",
                  "value": null,
                  "createdAtUtc": "2026-09-05T01:02:04+00:00"
                },
                {
                  "version": 1,
                  "operation": "Set",
                  "value": "history-value",
                  "createdAtUtc": "2026-09-05T01:02:03+00:00"
                }
              ]
            }
            """));
        using var client = new ManagementClient(BaseAddress, handler);

        ConfigurationHistoryResponse response = await client.GetHistoryAsync(
            "UAT + Green",
            "Demo:Message #1");

        Assert.Equal(
            "/api/v1/management/configurations/history?environment=UAT%20%2B%20Green&key=Demo%3AMessage%20%231",
            handler.RequestPathAndQuery);
        Assert.Equal(new long[] { 2L, 1L },
            response.Versions.Select(static version => version.Version));
        Assert.Equal(new string[] { "Remove", "Set" },
            response.Versions.Select(static version => version.Operation));
    }

    [Fact]
    public async Task Current_query_rejects_non_success_without_exposing_query_or_body()
    {
        InvalidOperationException exception = await GetCurrentFailureAsync(
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("response-body-secret")
            });

        AssertSafeFailure(
            exception,
            "GET /api/v1/management/configurations/current returned status 403.");
    }

    [Fact]
    public async Task Current_query_rejects_the_wrong_media_type_without_exposing_the_body()
    {
        InvalidOperationException exception = await GetCurrentFailureAsync(
            JsonResponse("response-body-secret", "text/plain"));

        AssertSafeFailure(
            exception,
            "GET /api/v1/management/configurations/current returned status 200 with an invalid media type.");
    }

    [Fact]
    public async Task Current_query_rejects_a_null_contract_without_exposing_the_body()
    {
        InvalidOperationException exception = await GetCurrentFailureAsync(JsonResponse("null"));

        AssertSafeFailure(
            exception,
            "GET /api/v1/management/configurations/current returned status 200 without a response contract.");
    }

    [Fact]
    public async Task Current_query_rejects_the_wrong_contract_type_without_exposing_the_body()
    {
        InvalidOperationException exception = await GetCurrentFailureAsync(JsonResponse(
            """
            { "environment": ["response-body-secret"] }
            """));

        AssertSafeFailure(
            exception,
            "GET /api/v1/management/configurations/current returned status 200 with an invalid response contract.");
    }

    [Fact]
    public async Task Current_query_hides_transport_failure_details()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("transport-raw-output-secret"));
        using var client = new ManagementClient(BaseAddress, handler);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCurrentAsync("query-environment-secret", "query-key-secret"));

        AssertSafeFailure(
            exception,
            "GET /api/v1/management/configurations/current failed.");
        Assert.DoesNotContain("transport-raw-output-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_query_hides_response_body_read_failure_details(bool httpFailure)
    {
        const string bodyReadFailureSecret = "response-body-read-failure-secret";
        using var handler = new StubHttpMessageHandler(_ => StreamResponse(
            new FailingReadStream(() => httpFailure
                ? new HttpRequestException(bodyReadFailureSecret)
                : new IOException(bodyReadFailureSecret))));
        using var client = new ManagementClient(BaseAddress, handler);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCurrentAsync("query-environment-secret", "query-key-secret"));

        AssertSafeFailure(
            exception,
            "GET /api/v1/management/configurations/current failed.");
        Assert.DoesNotContain(bodyReadFailureSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_query_times_out_when_response_body_stalls_without_exposing_query_or_body()
    {
        using var handler = new StubHttpMessageHandler(_ => BlockingResponse());
        using var client = new ManagementClient(
            BaseAddress,
            handler,
            TimeSpan.FromMilliseconds(250));
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GetCurrentAsync(
                "query-environment-secret",
                "query-key-secret",
                callerCancellation.Token));

        Assert.Equal("GET /api/v1/management/configurations/current timed out.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("query-environment-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("query-key-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_query_preserves_caller_cancellation_when_response_body_stalls()
    {
        using var handler = new StubHttpMessageHandler(_ => BlockingResponse());
        using var client = new ManagementClient(BaseAddress, handler, TimeSpan.FromSeconds(3));
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetCurrentAsync(
                "query-environment-secret",
                "query-key-secret",
                callerCancellation.Token));

        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Set_and_remove_hide_response_body_transport_failure_details(
        bool remove,
        bool httpFailure)
    {
        const string responseBodyFailureSecret = "mutation-response-body-failure-secret";
        using var handler = new StubHttpMessageHandler(_ => CreatedResponse(
            new FailingHttpContent(() => httpFailure
                ? new HttpRequestException(responseBodyFailureSecret)
                : new IOException(responseBodyFailureSecret))));
        using var client = new ManagementClient(BaseAddress, handler);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SendMutationAsync(client, remove));

        AssertSafeMutationFailure(
            exception,
            remove ? "/api/v1/management/configurations/remove" : "/api/v1/management/configurations/set");
        Assert.DoesNotContain(responseBodyFailureSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Set_and_remove_time_out_when_response_body_stalls(bool remove)
    {
        using var handler = new StubHttpMessageHandler(_ => CreatedResponse(new BlockingHttpContent()));
        using var client = new ManagementClient(
            BaseAddress,
            handler,
            TimeSpan.FromMilliseconds(250));
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            SendMutationAsync(client, remove, callerCancellation.Token));

        string route = remove
            ? "/api/v1/management/configurations/remove"
            : "/api/v1/management/configurations/set";
        Assert.Equal($"POST {route} timed out.", exception.Message);
        Assert.Null(exception.InnerException);
        AssertSafeMutationText(exception.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Set_and_remove_preserve_caller_cancellation_when_response_body_stalls(
        bool remove)
    {
        using var handler = new StubHttpMessageHandler(_ => CreatedResponse(new BlockingHttpContent()));
        using var client = new ManagementClient(BaseAddress, handler, TimeSpan.FromSeconds(3));
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SendMutationAsync(client, remove, callerCancellation.Token));

        Assert.Equal(callerCancellation.Token, exception.CancellationToken);
    }

    private static async Task<InvalidOperationException> GetCurrentFailureAsync(
        HttpResponseMessage response)
    {
        using var handler = new StubHttpMessageHandler(_ => response);
        using var client = new ManagementClient(BaseAddress, handler);
        return await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCurrentAsync("query-environment-secret", "query-key-secret"));
    }

    private static Task<ManagementCommandResponse> SendMutationAsync(
        ManagementClient client,
        bool remove,
        CancellationToken cancellationToken = default) =>
        remove
            ? client.RemoveAsync(
                "query-environment-secret",
                "query-key-secret",
                cancellationToken)
            : client.SetAsync(
                "query-environment-secret",
                "query-key-secret",
                "configuration-value-secret",
                cancellationToken);

    private static void AssertSafeFailure(
        InvalidOperationException exception,
        string expectedMessage)
    {
        Assert.Equal(expectedMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("query-environment-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("query-key-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("response-body-secret", exception.ToString(), StringComparison.Ordinal);
    }

    private static void AssertSafeMutationFailure(
        InvalidOperationException exception,
        string route)
    {
        Assert.Equal($"POST {route} failed.", exception.Message);
        Assert.Null(exception.InnerException);
        AssertSafeMutationText(exception.ToString());
    }

    private static void AssertSafeMutationText(string text)
    {
        Assert.DoesNotContain("query-environment-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("query-key-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration-value-secret", text, StringComparison.Ordinal);
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
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage BlockingResponse() =>
        new(HttpStatusCode.OK) { Content = new BlockingHttpContent() };

    private static HttpResponseMessage CreatedResponse(HttpContent content) =>
        new(HttpStatusCode.Created) { Content = content };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        internal string? RequestPathAndQuery { get; private set; }

        internal string[] ApiKeyHeaderValues { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPathAndQuery = request.RequestUri?.PathAndQuery;
            ApiKeyHeaderValues = request.Headers.TryGetValues("X-Api-Key", out var values) ? values.ToArray() : [];
            return Task.FromResult(createResponse(request));
        }
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

    private sealed class BlockingReadStream : Stream
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

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith(static _ => 0, cancellationToken);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingHttpContent : HttpContent
    {
        internal BlockingHttpContent()
        {
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingReadStream());

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new BlockingReadStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FailingHttpContent(Func<Exception> createException) : HttpContent
    {
        internal FailingHttpContent()
            : this(static () => new IOException())
        {
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(createException());

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.FromException(createException());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
