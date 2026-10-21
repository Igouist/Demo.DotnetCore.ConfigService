using System.Net;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class OpenApiDocumentTests
{
    [Theory]
    [InlineData("/api/v1/management/configurations/set", "post", "201", true)]
    [InlineData("/api/v1/management/configurations/remove", "post", "201", true)]
    [InlineData("/api/v1/management/configurations", "get", "200", false)]
    [InlineData("/api/v1/management/configurations/current", "get", "200", false)]
    [InlineData("/api/v1/management/configurations/history", "get", "200", false)]
    [InlineData("/api/v1/distribution/configurations/resolve", "post", "200", true)]
    public async Task Document_describes_the_request_and_responses_of_each_endpoint(
        string path,
        string method,
        string successStatus,
        bool hasJsonBody)
    {
        using JsonDocument document = await GetDocumentAsync();

        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method);
        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()));
        Assert.Equal(hasJsonBody, operation.TryGetProperty("requestBody", out JsonElement requestBody));
        if (hasJsonBody)
        {
            Assert.True(requestBody.GetProperty("required").GetBoolean());
            JsonElement content = requestBody.GetProperty("content").GetProperty("application/json");
            Assert.True(content.TryGetProperty("schema", out _));
            Assert.True(content.TryGetProperty("example", out _));
        }

        JsonElement responses = operation.GetProperty("responses");
        Assert.True(responses
            .GetProperty(successStatus)
            .GetProperty("content")
            .TryGetProperty("application/json", out _));
        JsonElement validationFailure = responses.GetProperty("400");
        Assert.True(validationFailure
            .GetProperty("content")
            .TryGetProperty("application/problem+json", out _));
        Assert.Contains("validation_failed", validationFailure.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Document_lists_only_the_management_and_distribution_endpoints()
    {
        using JsonDocument document = await GetDocumentAsync();

        string[] paths = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(static path => path.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "/api/v1/distribution/configurations/resolve",
                "/api/v1/management/configurations",
                "/api/v1/management/configurations/current",
                "/api/v1/management/configurations/history",
                "/api/v1/management/configurations/remove",
                "/api/v1/management/configurations/set"
            ],
            paths);
    }

    [Fact]
    public async Task Swagger_ui_is_served()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
