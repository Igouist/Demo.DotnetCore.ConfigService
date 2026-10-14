using System.Net;
using System.Text;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class ManagementHttpContractTests
{
    private const string Timestamp = "2026-09-02T01:02:03+00:00";

    [Fact]
    public async Task Management_routes_return_the_successful_command_current_and_history_contracts()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var setResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/set",
                   """
                   {"environment":"Production","key":"Demo:Message","value":"central-value"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, setResponse.StatusCode);
            Assert.Null(setResponse.Headers.Location);
            using var setBody = await ReadJsonAsync(setResponse);
            AssertCommandResponse(setBody.RootElement, "Production", "Demo:Message", 1, "Present");
        }

        using (var setEmptyResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/set",
                   """
                   {"environment":"Production","key":"Demo:Empty","value":""}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, setEmptyResponse.StatusCode);
            Assert.Null(setEmptyResponse.Headers.Location);
            using var setEmptyBody = await ReadJsonAsync(setEmptyResponse);
            AssertCommandResponse(setEmptyBody.RootElement, "Production", "Demo:Empty", 1, "Present");
        }

        using (var currentPresentResponse = await client.GetAsync(
                   "/api/v1/management/configurations/current?environment=Production&key=Demo:Empty"))
        {
            Assert.Equal(HttpStatusCode.OK, currentPresentResponse.StatusCode);
            using var currentPresentBody = await ReadJsonAsync(currentPresentResponse);
            AssertCurrentResponse(
                currentPresentBody.RootElement,
                "Production",
                "Demo:Empty",
                1,
                "Present",
                JsonValueKind.String,
                string.Empty);
        }

        using (var removeResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/remove",
                   """
                   {"environment":"pRoDuCtIoN","key":"dEmO:mEsSaGe"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, removeResponse.StatusCode);
            Assert.Null(removeResponse.Headers.Location);
            using var removeBody = await ReadJsonAsync(removeResponse);
            AssertCommandResponse(removeBody.RootElement, "Production", "Demo:Message", 2, "Removed");
        }

        using (var currentRemovedResponse = await client.GetAsync(
                   "/api/v1/management/configurations/current?environment=production&key=demo:message"))
        {
            Assert.Equal(HttpStatusCode.OK, currentRemovedResponse.StatusCode);
            using var currentRemovedBody = await ReadJsonAsync(currentRemovedResponse);
            AssertCurrentResponse(
                currentRemovedBody.RootElement,
                "Production",
                "Demo:Message",
                2,
                "Removed",
                JsonValueKind.Null,
                null);
        }

        using var historyResponse = await client.GetAsync(
            "/api/v1/management/configurations/history?environment=PRODUCTION&key=DEMO:MESSAGE");

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var historyBody = await ReadJsonAsync(historyResponse);
        AssertObjectProperties(historyBody.RootElement, "environment", "key", "versions");
        Assert.Equal("Production", historyBody.RootElement.GetProperty("environment").GetString());
        Assert.Equal("Demo:Message", historyBody.RootElement.GetProperty("key").GetString());

        var versions = historyBody.RootElement.GetProperty("versions");
        Assert.Equal(JsonValueKind.Array, versions.ValueKind);
        Assert.Equal(2, versions.GetArrayLength());
        AssertVersionResponse(versions[0], 2, "Remove", JsonValueKind.Null, null);
        AssertVersionResponse(versions[1], 1, "Set", JsonValueKind.String, "central-value");
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json) =>
        await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertCommandResponse(
        JsonElement response,
        string environment,
        string key,
        long version,
        string state)
    {
        AssertObjectProperties(response, "environment", "key", "version", "state", "createdAtUtc");
        Assert.Equal(environment, response.GetProperty("environment").GetString());
        Assert.Equal(key, response.GetProperty("key").GetString());
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(state, response.GetProperty("state").GetString());
        Assert.Equal(Timestamp, response.GetProperty("createdAtUtc").GetString());
    }

    private static void AssertCurrentResponse(
        JsonElement response,
        string environment,
        string key,
        long version,
        string state,
        JsonValueKind valueKind,
        string? value)
    {
        AssertObjectProperties(response, "environment", "key", "version", "state", "value", "createdAtUtc");
        Assert.Equal(environment, response.GetProperty("environment").GetString());
        Assert.Equal(key, response.GetProperty("key").GetString());
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(state, response.GetProperty("state").GetString());
        var valueElement = response.GetProperty("value");
        Assert.Equal(valueKind, valueElement.ValueKind);
        Assert.Equal(value, valueElement.GetString());
        Assert.Equal(Timestamp, response.GetProperty("createdAtUtc").GetString());
    }

    private static void AssertVersionResponse(
        JsonElement response,
        long version,
        string operation,
        JsonValueKind valueKind,
        string? value)
    {
        AssertObjectProperties(response, "version", "operation", "value", "createdAtUtc");
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(operation, response.GetProperty("operation").GetString());
        var valueElement = response.GetProperty("value");
        Assert.Equal(valueKind, valueElement.ValueKind);
        Assert.Equal(value, valueElement.GetString());
        Assert.Equal(Timestamp, response.GetProperty("createdAtUtc").GetString());
    }

    private static void AssertObjectProperties(JsonElement value, params string[] expectedProperties)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(
            expectedProperties.OrderBy(static property => property, StringComparer.Ordinal),
            value.EnumerateObject().Select(static property => property.Name).OrderBy(static property => property, StringComparer.Ordinal));
    }
}
