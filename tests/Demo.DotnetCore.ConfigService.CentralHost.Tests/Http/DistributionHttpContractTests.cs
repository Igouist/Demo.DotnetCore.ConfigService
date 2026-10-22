using System.Net;
using System.Text;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

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
        AssertObjectProperties(resolveBody.RootElement, "values", "missingKeys", "revision");

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
        AssertObjectProperties(resolveBody.RootElement, "values", "missingKeys", "revision");
        var values = resolveBody.RootElement.GetProperty("values");
        AssertObjectProperties(values, "Demo:Empty");
        Assert.Equal(string.Empty, values.GetProperty("Demo:Empty").GetString());
        Assert.Empty(resolveBody.RootElement.GetProperty("missingKeys").EnumerateArray());
    }

    [Fact]
    public async Task Resolve_revision_tracks_requested_key_versions_and_is_logged_with_them()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var sourceFactory = new CentralHostWebApplicationFactory();
        await using var factory = sourceFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddProvider(loggerProvider);
            }));
        using var client = factory.CreateClient();
        const string request = """{"environment":"UAT","keys":["Demo:Message","Demo:NeverCreated"]}""";
        const string respelledRequest = """{"environment":"uat","keys":["demo:nevercreated","DEMO:MESSAGE"]}""";

        await AssertCreatedAsync(client, """{"environment":"UAT","key":"Demo:Message","value":"first-secret-value"}""");
        string first = await ResolveRevisionAsync(client, request);
        string respelled = await ResolveRevisionAsync(client, respelledRequest);
        string otherKeys = await ResolveRevisionAsync(client, """{"environment":"UAT","keys":["Demo:Message"]}""");
        await AssertCreatedAsync(client, """{"environment":"UAT","key":"Demo:Message","value":"first-secret-value"}""");
        string afterSameValueSet = await ResolveRevisionAsync(client, request);

        Assert.Matches("^r1-[0-9a-f]{24}$", first);
        Assert.Equal(first, respelled);
        Assert.NotEqual(first, otherKeys);
        Assert.NotEqual(first, afterSameValueSet);

        var resolveLogs = loggerProvider.Entries
            .Where(static entry => entry.Category == typeof(DistributionConfigurationsController).FullName)
            .ToArray();
        Assert.Equal(4, resolveLogs.Length);
        Assert.All(resolveLogs, static entry => Assert.Equal(LogLevel.Information, entry.Level));
        var firstLog = resolveLogs[0];
        Assert.Equal(first, firstLog.Get("Revision"));
        Assert.Equal("UAT", firstLog.Get("Environment"));
        Assert.False(string.IsNullOrEmpty(firstLog.Get("TraceIdentifier") as string));
        Assert.Equal(["Demo:Message=1", "Demo:NeverCreated=0"], Assert.IsAssignableFrom<IEnumerable<string>>(firstLog.Get("KeyVersions")));
        Assert.Equal(["Demo:Message=2", "Demo:NeverCreated=0"], Assert.IsAssignableFrom<IEnumerable<string>>(resolveLogs[3].Get("KeyVersions")));
        Assert.DoesNotContain(
            loggerProvider.Entries,
            static entry => entry.Message.Contains("first-secret-value", StringComparison.Ordinal));
    }

    private static async Task<string> ResolveRevisionAsync(HttpClient client, string json)
    {
        using var response = await PostJsonAsync(client, "/api/v1/distribution/configurations/resolve", json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("revision").GetString()!;
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

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose() { }
    }

    private sealed record CapturedLogEntry(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Fields)
    {
        public object? Get(string name) => Fields.SingleOrDefault(pair => pair.Key == name).Value;
    }

    private sealed class CapturingLogger(string category, List<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToArray() : [];
            lock (entries)
            {
                entries.Add(new CapturedLogEntry(category, logLevel, formatter(state, exception), fields));
            }
        }
    }
}
