using System.Net;
using System.Text;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class CentralHostLifecycleTests
{
    [Fact]
    public async Task Central_host_preserves_one_entry_lifecycle_through_its_http_contract()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var firstTimestamp = new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero);
        var removalTimestamp = firstTimestamp.AddMinutes(1);
        var restoreTimestamp = removalTimestamp.AddMinutes(1);

        using (var setResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/set",
                   """
                   {"environment":"UAT","key":"Demo:Message","value":"central-value","changedBy":"test-author"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, setResponse.StatusCode);
            Assert.Null(setResponse.Headers.Location);
            using var setBody = await ReadJsonAsync(setResponse);
            AssertCommandResponse(setBody.RootElement, "UAT", "Demo:Message", 1, "Present", firstTimestamp);
        }

        using (var currentResponse = await client.GetAsync(
                   "/api/v1/management/configurations/current?environment=uat&key=demo%3Amessage"))
        {
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
            using var currentBody = await ReadJsonAsync(currentResponse);
            AssertCurrentResponse(
                currentBody.RootElement,
                "UAT",
                "Demo:Message",
                1,
                "Present",
                JsonValueKind.String,
                "central-value",
                firstTimestamp);
        }

        using (var resolveResponse = await PostJsonAsync(
                   client,
                   "/api/v1/distribution/configurations/resolve",
                   """
                   {"environment":"uat","keys":["demo:message","Demo:NeverCreated"]}
                   """))
        {
            Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
            using var resolveBody = await ReadJsonAsync(resolveResponse);
            AssertTopLevelMembers(resolveBody.RootElement, "values", "missingKeys", "revision");

            var values = resolveBody.RootElement.GetProperty("values");
            AssertTopLevelMembers(values, "Demo:Message");
            Assert.Equal("central-value", values.GetProperty("Demo:Message").GetString());

            var missingKeys = resolveBody.RootElement.GetProperty("missingKeys");
            Assert.Equal(JsonValueKind.Array, missingKeys.ValueKind);
            Assert.Equal(["Demo:NeverCreated"], missingKeys.EnumerateArray().Select(static key => key.GetString()));
        }

        factory.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        using (var removeResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/remove",
                   """
                   {"environment":"uat","key":"demo:message","changedBy":"test-author"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, removeResponse.StatusCode);
            Assert.Null(removeResponse.Headers.Location);
            using var removeBody = await ReadJsonAsync(removeResponse);
            AssertCommandResponse(removeBody.RootElement, "UAT", "Demo:Message", 2, "Removed", removalTimestamp);
        }

        using (var resolveRemovedResponse = await PostJsonAsync(
                   client,
                   "/api/v1/distribution/configurations/resolve",
                   """
                   {"environment":"UAT","keys":["demo:message"]}
                   """))
        {
            Assert.Equal(HttpStatusCode.OK, resolveRemovedResponse.StatusCode);
            using var resolveRemovedBody = await ReadJsonAsync(resolveRemovedResponse);
            AssertTopLevelMembers(resolveRemovedBody.RootElement, "values", "missingKeys", "revision");
            AssertTopLevelMembers(resolveRemovedBody.RootElement.GetProperty("values"));
            Assert.Equal(
                ["Demo:Message"],
                resolveRemovedBody.RootElement.GetProperty("missingKeys").EnumerateArray().Select(static key => key.GetString()));
        }

        factory.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        using (var restoreResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/set",
                   """
                   {"environment":"uat","key":"DEMO:MESSAGE","value":"","changedBy":"test-author"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, restoreResponse.StatusCode);
            Assert.Null(restoreResponse.Headers.Location);
            using var restoreBody = await ReadJsonAsync(restoreResponse);
            AssertCommandResponse(restoreBody.RootElement, "UAT", "Demo:Message", 3, "Present", restoreTimestamp);
        }

        using (var historyResponse = await client.GetAsync(
                   "/api/v1/management/configurations/history?environment=UAT&key=demo%3Amessage"))
        {
            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
            using var historyBody = await ReadJsonAsync(historyResponse);
            AssertTopLevelMembers(historyBody.RootElement, "environment", "key", "versions");
            Assert.Equal("UAT", historyBody.RootElement.GetProperty("environment").GetString());
            Assert.Equal("Demo:Message", historyBody.RootElement.GetProperty("key").GetString());

            var versions = historyBody.RootElement.GetProperty("versions");
            Assert.Equal(JsonValueKind.Array, versions.ValueKind);
            Assert.Equal(3, versions.GetArrayLength());
            AssertVersionResponse(versions[0], 3, "Set", JsonValueKind.String, string.Empty, restoreTimestamp);
            AssertVersionResponse(versions[1], 2, "Remove", JsonValueKind.Null, null, removalTimestamp);
            AssertVersionResponse(versions[2], 1, "Set", JsonValueKind.String, "central-value", firstTimestamp);

            var createdAtUtc = versions
                .EnumerateArray()
                .Select(static version => version.GetProperty("createdAtUtc").GetDateTimeOffset())
                .Reverse()
                .ToArray();
            Assert.True(createdAtUtc[0] < createdAtUtc[1]);
            Assert.True(createdAtUtc[1] < createdAtUtc[2]);
        }

        using var finalCurrentResponse = await client.GetAsync(
            "/api/v1/management/configurations/current?environment=UAT&key=Demo%3AMessage");

        Assert.Equal(HttpStatusCode.OK, finalCurrentResponse.StatusCode);
        using var finalCurrentBody = await ReadJsonAsync(finalCurrentResponse);
        AssertCurrentResponse(
            finalCurrentBody.RootElement,
            "UAT",
            "Demo:Message",
            3,
            "Present",
            JsonValueKind.String,
            string.Empty,
            restoreTimestamp);
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
        string state,
        DateTimeOffset createdAtUtc)
    {
        AssertTopLevelMembers(response, "environment", "key", "version", "state", "createdAtUtc");
        Assert.Equal(environment, response.GetProperty("environment").GetString());
        Assert.Equal(key, response.GetProperty("key").GetString());
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(state, response.GetProperty("state").GetString());
        Assert.Equal(createdAtUtc, response.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    private static void AssertCurrentResponse(
        JsonElement response,
        string environment,
        string key,
        long version,
        string state,
        JsonValueKind valueKind,
        string? value,
        DateTimeOffset createdAtUtc)
    {
        AssertTopLevelMembers(response, "environment", "key", "version", "state", "value", "createdAtUtc");
        Assert.Equal(environment, response.GetProperty("environment").GetString());
        Assert.Equal(key, response.GetProperty("key").GetString());
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(state, response.GetProperty("state").GetString());
        var valueElement = response.GetProperty("value");
        Assert.Equal(valueKind, valueElement.ValueKind);
        Assert.Equal(value, valueElement.GetString());
        Assert.Equal(createdAtUtc, response.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    private static void AssertVersionResponse(
        JsonElement response,
        long version,
        string operation,
        JsonValueKind valueKind,
        string? value,
        DateTimeOffset createdAtUtc)
    {
        AssertTopLevelMembers(response, "version", "operation", "value", "createdAtUtc", "changedBy", "reason");
        Assert.Equal("test-author", response.GetProperty("changedBy").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("reason").ValueKind);
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(operation, response.GetProperty("operation").GetString());
        var valueElement = response.GetProperty("value");
        Assert.Equal(valueKind, valueElement.ValueKind);
        Assert.Equal(value, valueElement.GetString());
        Assert.Equal(createdAtUtc, response.GetProperty("createdAtUtc").GetDateTimeOffset());
    }

    private static void AssertTopLevelMembers(JsonElement value, params string[] expectedMembers)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(
            expectedMembers.OrderBy(static member => member, StringComparer.Ordinal),
            value.EnumerateObject().Select(static property => property.Name).OrderBy(static member => member, StringComparer.Ordinal));
    }
}
