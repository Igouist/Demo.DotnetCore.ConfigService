using System.Net;
using System.Text;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class DistributionHttpContractTests
{
    [Fact]
    public async Task Resolve_returns_only_present_values_and_missing_keys()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        await AssertCreatedAsync(client, """
                                         {"environment":"Production","key":"Demo:Message","value":"central-value"}
                                         """);
        await AssertCreatedAsync(client, """
                                         {"environment":"Production","key":"Demo:Removed","value":"transient-value"}
                                         """);
        await AssertCreatedAsync(
            client,
            """
            {"environment":"production","key":"demo:removed"}
            """,
            "/api/v1/management/configurations/remove");

        using var resolveResponse = await PostJsonAsync(
            client,
            "/api/v1/distribution/configurations/resolve",
            """
            {"environment":"PRODUCTION","keys":["Demo:Message","Demo:Removed","demo:NeverCreated"]}
            """);

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        using var resolveBody = await ReadJsonAsync(resolveResponse);
        AssertObjectProperties(resolveBody.RootElement, "values", "missingKeys");

        var values = resolveBody.RootElement.GetProperty("values");
        AssertObjectProperties(values, "Demo:Message");
        Assert.Equal("central-value", values.GetProperty("Demo:Message").GetString());

        var missingKeys = resolveBody.RootElement.GetProperty("missingKeys");
        Assert.Equal(JsonValueKind.Array, missingKeys.ValueKind);
        Assert.Equal(["Demo:Removed", "demo:NeverCreated"], missingKeys.EnumerateArray().Select(static key => key.GetString()));
    }

    [Fact]
    public async Task Resolve_keeps_empty_strings_in_values()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        await AssertCreatedAsync(client, """
                                         {"environment":"Production","key":"Demo:Empty","value":""}
                                         """);

        using var resolveResponse = await PostJsonAsync(
            client,
            "/api/v1/distribution/configurations/resolve",
            """
            {"environment":"Production","keys":["Demo:Empty"]}
            """);

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        using var resolveBody = await ReadJsonAsync(resolveResponse);
        AssertObjectProperties(resolveBody.RootElement, "values", "missingKeys");
        var values = resolveBody.RootElement.GetProperty("values");
        AssertObjectProperties(values, "Demo:Empty");
        Assert.Equal(string.Empty, values.GetProperty("Demo:Empty").GetString());
        Assert.Empty(resolveBody.RootElement.GetProperty("missingKeys").EnumerateArray());
    }

    private static async Task AssertCreatedAsync(HttpClient client, string json, string path = "/api/v1/management/configurations/set")
    {
        using var response = await PostJsonAsync(client, path, json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json) =>
        await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertObjectProperties(JsonElement value, params string[] expectedProperties)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(
            expectedProperties.OrderBy(static property => property, StringComparer.Ordinal),
            value.EnumerateObject().Select(static property => property.Name).OrderBy(static property => property, StringComparer.Ordinal));
    }
}
