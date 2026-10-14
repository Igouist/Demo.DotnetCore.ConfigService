using System.Net;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.DemoHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.DemoHost.Tests.Http;

public sealed class DemoConfigurationHttpTests
{
    [Fact]
    public async Task Scoped_route_returns_only_relative_demo_values_and_no_store()
    {
        await using var factory = new DemoHostWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Demo"] = "root-excluded",
                ["Demo:Empty"] = string.Empty,
                ["Demo:Parent"] = "parent-value",
                ["Demo:Parent:Child"] = "child-value",
                ["Outside:Value"] = "outside-value"
            });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/demo/effective-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertProperties(body.RootElement, "scope", "effectiveValues");
        Assert.Equal("Demo", body.RootElement.GetProperty("scope").GetString());
        JsonElement values = body.RootElement.GetProperty("effectiveValues");
        Assert.Equal(string.Empty, values.GetProperty("Empty").GetString());
        Assert.Equal("parent-value", values.GetProperty("Parent").GetString());
        Assert.Equal("child-value", values.GetProperty("Parent:Child").GetString());
        Assert.False(values.TryGetProperty("Demo", out _));
        Assert.False(values.TryGetProperty("Outside:Value", out _));
    }

    [Fact]
    public async Task Scoped_route_returns_an_empty_object_when_every_demo_value_is_null()
    {
        await using var factory = new DemoHostWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Demo:FallbackAllowedMessage"] = null,
                ["Demo:LocalOnlyMessage"] = null
            });
        using HttpClient client = factory.CreateClient();

        using JsonDocument body = await GetJsonAsync(
            client,
            "/api/v1/demo/effective-configuration");

        Assert.Empty(body.RootElement.GetProperty("effectiveValues").EnumerateObject());
    }

    [Fact]
    public async Task Unsafe_route_is_always_mapped_and_has_its_distinct_warning_shape()
    {
        await using var factory = new DemoHostWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Outside:Value"] = "outside-value"
            });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/demo/unsafe/root-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertProperties(body.RootElement, "scope", "warning", "effectiveValues");
        Assert.Equal("Root", body.RootElement.GetProperty("scope").GetString());
        Assert.Equal(
            "This response may expose sensitive configuration values.",
            body.RootElement.GetProperty("warning").GetString());
        Assert.Equal(
            "outside-value",
            body.RootElement.GetProperty("effectiveValues").GetProperty("Outside:Value").GetString());
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
}
