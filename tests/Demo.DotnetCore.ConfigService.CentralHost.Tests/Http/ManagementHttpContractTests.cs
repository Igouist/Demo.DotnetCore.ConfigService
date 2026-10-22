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
                   {"environment":"Production","key":"Demo:Message","value":"central-value","changedBy":"test-author"}
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
                   {"environment":"Production","key":"Demo:Empty","value":"","changedBy":"test-author"}
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
                   {"environment":"pRoDuCtIoN","key":"dEmO:mEsSaGe","changedBy":"test-author"}
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

    [Fact]
    public async Task Environment_list_returns_every_key_with_its_latest_state_but_no_values()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        foreach (var (environment, key, value) in new[]
                 {
                     ("UAT", "Demo:Zeta", "zeta-value"),
                     ("UAT", "demo:alpha", "alpha-value"),
                     ("uat", "DEMO:ALPHA", "alpha-value-2"),
                     ("UAT", "Demo:Removed", "removed-value"),
                     ("Production", "Demo:OtherEnvironment", "other-value")
                 })
        {
            using var setResponse = await PostJsonAsync(
                client,
                "/api/v1/management/configurations/set",
                JsonSerializer.Serialize(new { environment, key, value, changedBy = "test-author" }));
            Assert.Equal(HttpStatusCode.Created, setResponse.StatusCode);
        }

        using (var removeResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/remove",
                   """
                   {"environment":"UAT","key":"Demo:Removed","changedBy":"test-author"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, removeResponse.StatusCode);
        }

        using var listResponse = await client.GetAsync("/api/v1/management/configurations?environment=uAt");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listText = await listResponse.Content.ReadAsStringAsync();
        using var listBody = await ReadJsonAsync(listResponse);
        AssertObjectProperties(listBody.RootElement, "environment", "configurations");
        Assert.Equal("uAt", listBody.RootElement.GetProperty("environment").GetString());
        var configurations = listBody.RootElement.GetProperty("configurations");
        Assert.Equal(JsonValueKind.Array, configurations.ValueKind);
        Assert.Equal(3, configurations.GetArrayLength());
        AssertSummaryResponse(configurations[0], "demo:alpha", 2, "Present");
        AssertSummaryResponse(configurations[1], "Demo:Removed", 2, "Removed");
        AssertSummaryResponse(configurations[2], "Demo:Zeta", 1, "Present");
        foreach (var forbidden in new[] { "alpha-value", "removed-value", "zeta-value", "OtherEnvironment" })
        {
            Assert.DoesNotContain(forbidden, listText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Environment_list_for_an_environment_without_entries_returns_an_empty_list()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/management/configurations?environment=NeverCreated");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        AssertObjectProperties(body.RootElement, "environment", "configurations");
        Assert.Equal("NeverCreated", body.RootElement.GetProperty("environment").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("configurations").GetArrayLength());
    }

    [Fact]
    public async Task History_returns_the_trimmed_change_attribution_of_each_version()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var setResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/set",
                   """{"environment":"UAT","key":"Demo:Attributed","value":"v1","changedBy":"  alice  ","reason":"  rotate key  "}"""))
        {
            Assert.Equal(HttpStatusCode.Created, setResponse.StatusCode);
        }

        using (var removeResponse = await PostJsonAsync(
                   client,
                   "/api/v1/management/configurations/remove",
                   """{"environment":"UAT","key":"Demo:Attributed","changedBy":"bob","reason":"   "}"""))
        {
            Assert.Equal(HttpStatusCode.Created, removeResponse.StatusCode);
        }

        using var historyResponse = await client.GetAsync(
            "/api/v1/management/configurations/history?environment=UAT&key=Demo:Attributed");
        using var historyBody = await ReadJsonAsync(historyResponse);
        var versions = historyBody.RootElement.GetProperty("versions").EnumerateArray().ToArray();

        Assert.Equal("bob", versions[0].GetProperty("changedBy").GetString());
        Assert.Equal(JsonValueKind.Null, versions[0].GetProperty("reason").ValueKind);
        Assert.Equal("alice", versions[1].GetProperty("changedBy").GetString());
        Assert.Equal("rotate key", versions[1].GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("/api/v1/management/configurations/set", """{"environment":"UAT","key":"Demo:Unattributed","value":"v1"}""")]
    [InlineData("/api/v1/management/configurations/remove", """{"environment":"UAT","key":"Demo:Unattributed"}""")]
    public async Task Commands_without_changed_by_are_rejected_before_writing(string path, string json)
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, path, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("changedBy", error.GetProperty("path").GetString());
        Assert.Equal("changed_by_required", error.GetProperty("code").GetString());
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

    private static void AssertSummaryResponse(
        JsonElement response,
        string key,
        long version,
        string state)
    {
        AssertObjectProperties(response, "key", "version", "state", "createdAtUtc");
        Assert.Equal(key, response.GetProperty("key").GetString());
        Assert.Equal(version, response.GetProperty("version").GetInt64());
        Assert.Equal(state, response.GetProperty("state").GetString());
        Assert.Equal(Timestamp, response.GetProperty("createdAtUtc").GetString());
    }

    private static void AssertVersionResponse(
        JsonElement response,
        long version,
        string operation,
        JsonValueKind valueKind,
        string? value)
    {
        AssertObjectProperties(response, "version", "operation", "value", "createdAtUtc", "changedBy", "reason");
        Assert.Equal("test-author", response.GetProperty("changedBy").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("reason").ValueKind);
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
