using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class CentralReadinessResponseReaderTests
{
    private const string ExpectedInstanceId = "794c1ca58c1342a6863d81e152777b17";
    private const string ForeignInstanceId = "00000000000000000000000000000001";

    [Fact]
    public async Task Reader_accepts_ready_for_the_expected_instance()
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ExpectedInstanceId}}"}""");

        CentralReadinessObservation observation =
            await CentralReadinessResponseReader.ReadAsync(
                response,
                ExpectedInstanceId);

        Assert.Equal(CentralReadinessObservation.Ready, observation);
    }

    [Fact]
    public async Task Reader_accepts_unavailable_for_the_expected_instance()
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.ServiceUnavailable,
            $$"""{"instanceId":"{{ExpectedInstanceId}}","status":"unavailable"}""");

        CentralReadinessObservation observation =
            await CentralReadinessResponseReader.ReadAsync(
                response,
                ExpectedInstanceId);

        Assert.Equal(CentralReadinessObservation.Unavailable, observation);
    }

    [Fact]
    public async Task Reader_identifies_a_contract_compatible_foreign_instance()
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ForeignInstanceId}}"}""");

        CentralReadinessObservation observation =
            await CentralReadinessResponseReader.ReadAsync(
                response,
                ExpectedInstanceId);

        Assert.Equal(CentralReadinessObservation.ForeignInstance, observation);
    }

    [Theory]
    [InlineData("""{"status":"ready"}""")]
    [InlineData("""{"status":"ready","instanceId":"794c1ca58c1342a6863d81e152777b17","extra":true}""")]
    [InlineData("""{"status":"ready","status":"ready","instanceId":"794c1ca58c1342a6863d81e152777b17"}""")]
    [InlineData("""{"status":"unavailable","instanceId":"794c1ca58c1342a6863d81e152777b17"}""")]
    [InlineData("""{"status":"ready","instanceId":"794C1CA58C1342A6863D81E152777B17"}""")]
    [InlineData("""{"status":"ready","instanceId":"not-a-guid"}""")]
    [InlineData("""{""")]
    public async Task Reader_rejects_an_inexact_contract_without_exposing_content(
        string body)
    {
        using HttpResponseMessage response = CreateResponse(HttpStatusCode.OK, body);

        CentralReadinessContractException exception =
            await Assert.ThrowsAsync<CentralReadinessContractException>(
                () => CentralReadinessResponseReader.ReadAsync(
                    response,
                    ExpectedInstanceId));

        Assert.Equal(
            "Central readiness response contract is invalid.",
            exception.Message);
        Assert.DoesNotContain(body, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ExpectedInstanceId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_rejects_a_missing_no_store_header()
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ExpectedInstanceId}}"}""",
            includeNoStore: false);

        await Assert.ThrowsAsync<CentralReadinessContractException>(
            () => CentralReadinessResponseReader.ReadAsync(
                response,
                ExpectedInstanceId));
    }

    [Fact]
    public async Task Reader_rejects_a_non_json_media_type()
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ExpectedInstanceId}}"}""");
        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("text/plain");

        await Assert.ThrowsAsync<CentralReadinessContractException>(
            () => CentralReadinessResponseReader.ReadAsync(
                response,
                ExpectedInstanceId));
    }

    [Theory]
    [InlineData("no-store", "no-store")]
    [InlineData("no-store, no-store", null)]
    [InlineData("no-store,", null)]
    [InlineData(",no-store", null)]
    [InlineData("no-store, public", null)]
    [InlineData("no-store", "public")]
    public async Task Reader_rejects_inexact_raw_cache_control(string first, string? second)
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ExpectedInstanceId}}"}""",
            includeNoStore: false);
        response.Headers.TryAddWithoutValidation(
            "Cache-Control", second is null ? [first] : [first, second]);

        await Assert.ThrowsAsync<CentralReadinessContractException>(
            () => CentralReadinessResponseReader.ReadAsync(response, ExpectedInstanceId));
    }

    [Theory]
    [InlineData("application/json", "text/plain")]
    [InlineData("application/json", "application/json")]
    [InlineData("application/json, text/plain", null)]
    [InlineData("application/json,", null)]
    [InlineData("application/json; charset=\"", null)]
    public async Task Reader_rejects_inexact_raw_content_type(string first, string? second)
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ExpectedInstanceId}}"}""");
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation(
            "Content-Type", second is null ? [first] : [first, second]);

        await Assert.ThrowsAsync<CentralReadinessContractException>(
            () => CentralReadinessResponseReader.ReadAsync(response, ExpectedInstanceId));
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", "no-store")]
    [InlineData("Application/JSON; charset=\"utf-8\"", "NO-STORE")]
    public async Task Reader_accepts_single_raw_headers_with_media_type_parameters(
        string contentType,
        string cacheControl)
    {
        using HttpResponseMessage response = CreateResponse(
            HttpStatusCode.OK,
            $$"""{"status":"ready","instanceId":"{{ExpectedInstanceId}}"}""",
            includeNoStore: false);
        response.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        Assert.Equal(CentralReadinessObservation.Ready,
            await CentralReadinessResponseReader.ReadAsync(response, ExpectedInstanceId));
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        string body,
        bool includeNoStore = true)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (includeNoStore)
        {
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        }

        return response;
    }
}
