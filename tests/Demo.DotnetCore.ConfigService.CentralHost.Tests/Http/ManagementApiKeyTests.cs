using System.Net;
using System.Text;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class ManagementApiKeyTests
{
    private const string SetBody = """{"environment":"UAT","key":"Demo:Guarded","value":"guarded-value","changedBy":"test-author"}""";

    public static TheoryData<string, string, string?> ManagementRequests => new()
    {
        { "POST", "/api/v1/management/configurations/set", SetBody },
        { "POST", "/api/v1/management/configurations/remove", """{"environment":"UAT","key":"Demo:Guarded","changedBy":"test-author"}""" },
        { "GET", "/api/v1/management/configurations?environment=UAT", null },
        { "GET", "/api/v1/management/configurations/current?environment=UAT&key=Demo:Guarded", null },
        { "GET", "/api/v1/management/configurations/history?environment=UAT&key=Demo:Guarded", null }
    };

    [Theory]
    [MemberData(nameof(ManagementRequests))]
    public async Task Every_management_route_rejects_a_missing_or_wrong_key_with_the_same_problem(
        string method,
        string path,
        string? body)
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Remove(ManagementApiKeyDefaults.HeaderName);
        using var missing = await SendAsync(client, method, path, body);
        client.DefaultRequestHeaders.Add(ManagementApiKeyDefaults.HeaderName, "wrong-key-sentinel");
        using var wrong = await SendAsync(client, method, path, body);

        string missingBody = await AssertUnauthorizedAsync(missing, path);
        string wrongBody = await AssertUnauthorizedAsync(wrong, path);
        Assert.DoesNotContain("wrong-key-sentinel", wrongBody, StringComparison.Ordinal);
        Assert.DoesNotContain(CentralHostWebApplicationFactory.ManagementApiKey, wrongBody, StringComparison.Ordinal);
        Assert.Equal(StripTraceId(missingBody), StripTraceId(wrongBody));
    }

    [Fact]
    public async Task Rejected_set_does_not_write()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var authorized = factory.CreateClient();
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Remove(ManagementApiKeyDefaults.HeaderName);

        using var rejected = await SendAsync(anonymous, "POST", "/api/v1/management/configurations/set", SetBody);
        using var current = await authorized.GetAsync(
            "/api/v1/management/configurations/current?environment=UAT&key=Demo:Guarded");

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
    }

    [Fact]
    public async Task Duplicate_key_headers_are_rejected()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ManagementApiKeyDefaults.HeaderName, CentralHostWebApplicationFactory.ManagementApiKey);

        using var response = await client.GetAsync("/api/v1/management/configurations?environment=UAT");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/v1/distribution/configurations/resolve", """{"environment":"UAT","keys":["Demo:Guarded"]}""")]
    [InlineData("GET", "/health/ready", null)]
    [InlineData("GET", "/swagger/v1/swagger.json", null)]
    [InlineData("GET", "/swagger/index.html", null)]
    public async Task Distribution_readiness_and_swagger_stay_open(string method, string path, string? body)
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ManagementApiKeyDefaults.HeaderName);

        using var response = await SendAsync(client, method, path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unconfigured_key_keeps_management_closed_and_logs_a_warning(string? configuredKey)
    {
        var loggerProvider = new WarningCapturingLoggerProvider();
        await using var sourceFactory = new CentralHostWebApplicationFactory();
        await using var factory = sourceFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ManagementApiKeyDefaults.ConfigurationKey, configuredKey);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ManagementApiKeyDefaults.ConfigurationKey] = configuredKey
                }));
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddProvider(loggerProvider);
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/management/configurations?environment=UAT");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            loggerProvider.Messages,
            static message => message.Contains(ManagementApiKeyDefaults.ConfigurationKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Swagger_document_declares_the_header_only_for_management_operations()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var root = document.RootElement;

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty(ManagementApiKeyDefaults.Scheme);
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal(ManagementApiKeyDefaults.HeaderName, scheme.GetProperty("name").GetString());

        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                bool isManagement = path.Name.StartsWith("/api/v1/management/", StringComparison.Ordinal);
                bool hasSecurity = operation.Value.TryGetProperty("security", out var security)
                    && security.EnumerateArray().Any(static requirement =>
                        requirement.TryGetProperty(ManagementApiKeyDefaults.Scheme, out _));
                Assert.Equal(isManagement, hasSecurity);
                Assert.Equal(isManagement, operation.Value.GetProperty("responses").TryGetProperty("401", out _));
            }
        }
    }

    private static async Task<string> AssertUnauthorizedAsync(HttpResponseMessage response, string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(
            ["code", "detail", "instance", "status", "title", "traceId", "type"],
            root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal("Unauthorized", root.GetProperty("title").GetString());
        Assert.Equal(401, root.GetProperty("status").GetInt32());
        Assert.Equal("unauthorized", root.GetProperty("code").GetString());
        Assert.Equal("A valid management API key is required.", root.GetProperty("detail").GetString());
        Assert.Equal(path.Split('?')[0], root.GetProperty("instance").GetString());
        return body;
    }

    private static string StripTraceId(string body)
    {
        using var document = JsonDocument.Parse(body);
        return string.Join(
            "|",
            document.RootElement.EnumerateObject()
                .Where(static property => property.Name != "traceId")
                .Select(static property => $"{property.Name}={property.Value}"));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request);
    }

    private sealed class WarningCapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose() { }

        private sealed class Logger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
