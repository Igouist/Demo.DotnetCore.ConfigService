using System.Net;
using System.Text;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class ProblemDetailsContractTests
{
    private const string SetPath = "/api/v1/management/configurations/set";
    private const string RemovePath = "/api/v1/management/configurations/remove";
    private const string CurrentPath = "/api/v1/management/configurations/current";
    private const string HistoryPath = "/api/v1/management/configurations/history";
    private const string ResolvePath = "/api/v1/distribution/configurations/resolve";

    public static TheoryData<string, string, string, string> TransportInvalidSetRequests => new()
    {
        { string.Empty, "$", "body_required", "A JSON request body is required." },
        { "{\"malformed-body-sentinel\":", "$", "invalid_json", "The request body must contain valid JSON." },
        { "{\"environment\":7,\"key\":\"Demo:TransportProbe\",\"value\":\"configuration-value-sentinel\"}", "$.environment", "invalid_type", "A request value has an incompatible JSON type." },
        { "{\"environment\":\"UAT\",\"key\":\"Demo:TransportProbe\",\"value\":\"configuration-value-sentinel\",\"unknown-member-sentinel\":\"unknown-value-sentinel\"}", "$", "unknown_property", "The request body contains an unknown property." }
    };

    public static TheoryData<string, string, string, string> ResolveSemanticInvalidRequests => new()
    {
        { "{\"environment\":\"UAT\",\"keys\":null}", "keys", "keys_required", "Keys are required." },
        { "{\"environment\":\"UAT\",\"keys\":[]}", "keys", "keys_empty", "Keys must contain at least one Configuration Key." },
        { "{\"environment\":\"UAT\",\"keys\":[\"Demo:Duplicate\",\"demo:duplicate\"]}", "keys", "duplicate_configuration_key", "Keys must not contain case-insensitive duplicates." },
        { "{\"environment\":\"UAT\",\"keys\":[\"CentralConfig:Reserved\"]}", "keys[0]", "invalid_configuration_key", "Configuration Key has an invalid structure." }
    };

    [Theory]
    [MemberData(nameof(TransportInvalidSetRequests))]
    public async Task Transport_invalid_Set_requests_return_safe_validation_problem_details_and_never_create_an_entry(
        string body,
        string errorPath,
        string errorCode,
        string errorMessage)
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var sourceFactory = new CentralHostWebApplicationFactory();
        await using var factory = CreateLoggingFactory(sourceFactory, loggerProvider);
        using var client = factory.CreateClient();

        using (var response = await client.PostAsync(SetPath, CreateJsonContent(body)))
        {
            var problem = await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "validation_failed",
                "One or more request fields are invalid.",
                SetPath,
                [(errorPath, errorCode, errorMessage)]);

            AssertDoesNotContain(problem.Body, "configuration-value-sentinel", "unknown-member-sentinel", "unknown-value-sentinel", "malformed-body-sentinel");
        }

        await AssertNeverCreatedCurrentAsync(client, "Demo:TransportProbe");
        AssertLogsDoNotContain(
            loggerProvider,
            "configuration-value-sentinel",
            "unknown-member-sentinel",
            "unknown-value-sentinel",
            "malformed-body-sentinel");
    }

    [Fact]
    public async Task Semantic_invalid_Set_returns_all_fixed_validation_errors()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var response = await PostJsonAsync(
                   client,
                   SetPath,
                   """
                   {"environment":" ","key":"CentralConfig:Reserved","value":null}
                   """))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "validation_failed",
                "One or more request fields are invalid.",
                SetPath,
                [
                    ("environment", "environment_required", "Environment is required."),
                    ("key", "invalid_configuration_key", "Configuration Key has an invalid structure."),
                    ("value", "value_required", "Value is required.")
                ]);
        }
    }

    [Fact]
    public async Task Semantic_invalid_Set_with_a_valid_identity_never_creates_that_identity()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var response = await PostJsonAsync(
                   client,
                   SetPath,
                   """
                   {"environment":"UAT","key":"Demo:SemanticProbe","value":null}
                   """))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "validation_failed",
                "One or more request fields are invalid.",
                SetPath,
                [("value", "value_required", "Value is required.")]);
        }

        await AssertNeverCreatedCurrentAsync(client, "Demo:SemanticProbe");
    }

    [Theory]
    [InlineData(CurrentPath)]
    [InlineData(HistoryPath)]
    public async Task Invalid_GET_query_fields_return_stable_validation_problem_details(string path)
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{path}?environment=UAT&key=CentralConfig:Reserved&query-sentinel=never-echo");

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "validation_failed",
            "One or more request fields are invalid.",
            path,
            [("key", "invalid_configuration_key", "Configuration Key has an invalid structure.")]);

        AssertDoesNotContain(problem.Body, "query-sentinel", "never-echo");
    }

    [Theory]
    [MemberData(nameof(ResolveSemanticInvalidRequests))]
    public async Task Resolve_semantic_invalid_requests_return_stable_validation_problem_details(
        string body,
        string errorPath,
        string errorCode,
        string errorMessage)
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, ResolvePath, body);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "validation_failed",
            "One or more request fields are invalid.",
            ResolvePath,
            [(errorPath, errorCode, errorMessage)]);
    }

    [Fact]
    public async Task Never_created_current_history_and_Remove_return_not_found_problem_details()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var current = await client.GetAsync($"{CurrentPath}?environment=UAT&key=Demo:NeverCreated"))
        {
            await AssertProblemAsync(
                current,
                HttpStatusCode.NotFound,
                "configuration_not_found",
                "The requested Configuration Entry was not found.",
                CurrentPath);
        }

        using (var history = await client.GetAsync($"{HistoryPath}?environment=UAT&key=Demo:NeverCreated"))
        {
            await AssertProblemAsync(
                history,
                HttpStatusCode.NotFound,
                "configuration_not_found",
                "The requested Configuration Entry was not found.",
                HistoryPath);
        }

        using var remove = await PostJsonAsync(
            client,
            RemovePath,
            """
            {"environment":"UAT","key":"Demo:NeverCreated"}
            """);
        await AssertProblemAsync(
            remove,
            HttpStatusCode.NotFound,
            "configuration_not_found",
            "The requested Configuration Entry was not found.",
            RemovePath);
    }

    [Fact]
    public async Task Repeated_Remove_returns_already_removed_problem_details()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var set = await PostJsonAsync(
                   client,
                   SetPath,
                   """
                   {"environment":"UAT","key":"Demo:RepeatedRemove","value":"central-value"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, set.StatusCode);
        }

        using (var remove = await PostJsonAsync(
                   client,
                   RemovePath,
                   """
                   {"environment":"UAT","key":"Demo:RepeatedRemove"}
                   """))
        {
            Assert.Equal(HttpStatusCode.Created, remove.StatusCode);
        }

        using var repeatedRemove = await PostJsonAsync(
            client,
            RemovePath,
            """
            {"environment":"UAT","key":"Demo:RepeatedRemove"}
            """);
        await AssertProblemAsync(
            repeatedRemove,
            HttpStatusCode.Conflict,
            "configuration_already_removed",
            "The requested Configuration Entry is already removed.",
            RemovePath);
    }

    [Fact]
    public async Task Injected_busy_persistence_failure_returns_a_retry_free_safe_problem_details_response()
    {
        var store = new ThrowingConfigurationEntryStore(
            new PersistenceBusyException(new InvalidOperationException("busy-exception-sentinel")));
        await using var sourceFactory = new CentralHostWebApplicationFactory();
        await using var factory = CreateFactory(sourceFactory, store, loggerProvider: null);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            SetPath,
            """
            {"environment":"UAT","key":"Demo:Busy","value":"configuration-value-sentinel"}
            """);

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "persistence_busy",
            "The configuration store is temporarily busy.",
            SetPath);

        Assert.Null(response.Headers.RetryAfter);
        AssertDoesNotContain(problem.Body, "busy-exception-sentinel", "configuration-value-sentinel");
    }

    [Theory]
    [InlineData((int)PersistenceFailureCategory.Locked)]
    [InlineData((int)PersistenceFailureCategory.Other)]
    public async Task Injected_persistence_failures_return_safe_problem_details_and_fixed_error_logs(int categoryValue)
    {
        var category = (PersistenceFailureCategory)categoryValue;
        var loggerProvider = new CapturingLoggerProvider();
        var store = new ThrowingConfigurationEntryStore(
            new PersistenceFailureException(
                category,
                new InvalidOperationException("persistence-exception-sentinel")));
        await using var sourceFactory = new CentralHostWebApplicationFactory();
        await using var factory = CreateFactory(sourceFactory, store, loggerProvider);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            SetPath,
            """
            {"environment":"UAT","key":"Demo:PersistenceFailure","value":"configuration-value-sentinel"}
            """);

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "persistence_failure",
            "The configuration store operation failed.",
            SetPath);

        var error = Assert.Single(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Null(error.Exception);
        AssertExactErrorLog(
            error,
            $"Central Host persistence failure. TraceIdentifier: {problem.TraceId}; PersistenceFailureCategory: {category}; Diagnostics: PersistenceFailureException -> InvalidOperationException",
            "Central Host persistence failure. TraceIdentifier: {TraceIdentifier}; PersistenceFailureCategory: {PersistenceFailureCategory}; Diagnostics: {Diagnostics}",
            ("TraceIdentifier", problem.TraceId),
            ("PersistenceFailureCategory", category),
            ("Diagnostics", "PersistenceFailureException -> InvalidOperationException"));
        AssertDoesNotContain(problem.Body, "persistence-exception-sentinel", "configuration-value-sentinel");
        AssertLogsDoNotContain(loggerProvider, "persistence-exception-sentinel", "configuration-value-sentinel");
    }

    [Fact]
    public async Task Unexpected_failures_redact_request_and_persistence_secrets_from_the_response_and_logs()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var sourceFactory = new CentralHostWebApplicationFactory();
        var connectionString = $"Data Source={sourceFactory.DatabasePath};Pooling=False";
        var store = new ThrowingConfigurationEntryStore(
            new InvalidOperationException(
                $"unexpected-exception-sentinel path={sourceFactory.DatabasePath} connection={connectionString} sql=SELECT sql-sentinel"));
        await using var factory = CreateFactory(sourceFactory, store, loggerProvider);
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(
            client,
            $"{SetPath}?query-sentinel=query-value-sentinel",
            """
            {"environment":"UAT","key":"Demo:BodySentinel","value":"configuration-value-sentinel"}
            """);

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "internal_error",
            "An unexpected application failure occurred.",
            SetPath);

        var error = Assert.Single(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Null(error.Exception);
        var stackTrace = error.GetStructuredValue("StackTrace")?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(stackTrace));
        AssertExactErrorLog(
            error,
            $"Central Host unexpected failure. TraceIdentifier: {problem.TraceId}; Diagnostics: InvalidOperationException; StackTrace: {stackTrace}",
            "Central Host unexpected failure. TraceIdentifier: {TraceIdentifier}; Diagnostics: {Diagnostics}; StackTrace: {StackTrace}",
            ("TraceIdentifier", problem.TraceId),
            ("Diagnostics", "InvalidOperationException"),
            ("StackTrace", stackTrace));
        AssertDoesNotContain(
            problem.Body,
            "configuration-value-sentinel",
            "BodySentinel",
            sourceFactory.DatabasePath,
            connectionString,
            "sql-sentinel",
            "query-sentinel",
            "query-value-sentinel",
            "unexpected-exception-sentinel");
        AssertLogsDoNotContain(
            loggerProvider,
            "configuration-value-sentinel",
            "BodySentinel",
            sourceFactory.DatabasePath,
            connectionString,
            "sql-sentinel",
            "query-sentinel",
            "query-value-sentinel",
            "unexpected-exception-sentinel");
    }

    [Fact]
    public async Task Response_started_failure_aborts_without_writing_a_problem_details_body_and_logs_only_safe_metadata()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var problemDetailsService = new RecordingProblemDetailsService();
        var writer = new CentralProblemDetailsWriter(problemDetailsService);
        var middleware = new CentralExceptionMiddleware(
            _ => Task.FromException(new InvalidOperationException(
                "response-started-exception-sentinel sql=started-sql-sentinel")),
            writer,
            loggerFactory.CreateLogger<CentralExceptionMiddleware>());
        var context = new DefaultHttpContext();
        var responseFeature = new StartedResponseFeature();
        var requestLifetime = new CapturingRequestLifetimeFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Features.Set<IHttpRequestLifetimeFeature>(requestLifetime);
        context.TraceIdentifier = "response-started-trace";
        context.Request.Path = SetPath;
        context.Request.QueryString = new QueryString("?query-sentinel=query-value-sentinel");
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("body-sentinel"));

        await middleware.InvokeAsync(context);

        Assert.True(requestLifetime.Aborted);
        Assert.True(responseFeature.HasStarted);
        Assert.Equal(0, problemDetailsService.WriteCallCount);
        var error = Assert.Single(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Null(error.Exception);
        var stackTrace = error.GetStructuredValue("StackTrace")?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(stackTrace));
        AssertExactErrorLog(
            error,
            $"Central Host failure after the response started. TraceIdentifier: response-started-trace; Diagnostics: InvalidOperationException; StackTrace: {stackTrace}",
            "Central Host failure after the response started. TraceIdentifier: {TraceIdentifier}; Diagnostics: {Diagnostics}; StackTrace: {StackTrace}",
            ("TraceIdentifier", "response-started-trace"),
            ("Diagnostics", "InvalidOperationException"),
            ("StackTrace", stackTrace));
        AssertLogsDoNotContain(
            loggerProvider,
            "response-started-exception-sentinel",
            "started-sql-sentinel",
            "body-sentinel",
            "query-sentinel",
            "query-value-sentinel");
    }

    [Fact]
    public async Task Problem_Details_writer_failure_before_response_start_aborts_without_recursion_and_logs_one_safe_error()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var problemDetailsService = new ThrowingProblemDetailsService(
            new InvalidOperationException("writer-before-exception-sentinel"));
        var writer = new CentralProblemDetailsWriter(problemDetailsService);
        var middleware = new CentralExceptionMiddleware(
            _ => Task.FromException(RequestValidationException.Single("$", "body_required", "A JSON request body is required.")),
            writer,
            loggerFactory.CreateLogger<CentralExceptionMiddleware>());
        var context = new DefaultHttpContext();
        var responseFeature = new MutableResponseFeature();
        var requestLifetime = new CapturingRequestLifetimeFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Features.Set<IHttpRequestLifetimeFeature>(requestLifetime);
        context.TraceIdentifier = "writer-before-trace";
        context.Request.Path = SetPath;
        context.Request.QueryString = new QueryString("?query-sentinel=query-value-sentinel");
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("body-sentinel"));

        await middleware.InvokeAsync(context);

        Assert.True(requestLifetime.Aborted);
        Assert.False(responseFeature.HasStarted);
        Assert.Equal(1, problemDetailsService.WriteCallCount);
        var error = Assert.Single(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Null(error.Exception);
        AssertExactErrorLog(
            error,
            "Central Host Problem Details writer failure. TraceIdentifier: writer-before-trace",
            "Central Host Problem Details writer failure. TraceIdentifier: {TraceIdentifier}",
            ("TraceIdentifier", "writer-before-trace"));
        AssertLogsDoNotContain(
            loggerProvider,
            "writer-before-exception-sentinel",
            "body-sentinel",
            "query-sentinel",
            "query-value-sentinel");
    }

    [Fact]
    public async Task Problem_Details_writer_failure_after_response_start_aborts_without_recursion_and_logs_one_safe_error()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var responseFeature = new MutableResponseFeature();
        var problemDetailsService = new ThrowingProblemDetailsService(
            new InvalidOperationException("writer-after-exception-sentinel"),
            _ => responseFeature.HasStarted = true);
        var writer = new CentralProblemDetailsWriter(problemDetailsService);
        var middleware = new CentralExceptionMiddleware(
            _ => Task.FromException(RequestValidationException.Single("$", "body_required", "A JSON request body is required.")),
            writer,
            loggerFactory.CreateLogger<CentralExceptionMiddleware>());
        var context = new DefaultHttpContext();
        var requestLifetime = new CapturingRequestLifetimeFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Features.Set<IHttpRequestLifetimeFeature>(requestLifetime);
        context.TraceIdentifier = "writer-after-trace";
        context.Request.Path = SetPath;
        context.Request.QueryString = new QueryString("?query-sentinel=query-value-sentinel");
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("body-sentinel"));

        await middleware.InvokeAsync(context);

        Assert.True(requestLifetime.Aborted);
        Assert.True(responseFeature.HasStarted);
        Assert.Equal(1, problemDetailsService.WriteCallCount);
        var error = Assert.Single(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
        Assert.Null(error.Exception);
        AssertExactErrorLog(
            error,
            "Central Host Problem Details writer failure. TraceIdentifier: writer-after-trace",
            "Central Host Problem Details writer failure. TraceIdentifier: {TraceIdentifier}",
            ("TraceIdentifier", "writer-after-trace"));
        AssertLogsDoNotContain(
            loggerProvider,
            "writer-after-exception-sentinel",
            "body-sentinel",
            "query-sentinel",
            "query-value-sentinel");
    }

    [Fact]
    public async Task Caller_requested_cancellation_is_rethrown_without_writing_or_logging_an_error()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var problemDetailsService = new RecordingProblemDetailsService();
        var writer = new CentralProblemDetailsWriter(problemDetailsService);
        var middleware = new CentralExceptionMiddleware(
            _ => Task.FromException(new OperationCanceledException("cancellation-sentinel")),
            writer,
            loggerFactory.CreateLogger<CentralExceptionMiddleware>());
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var context = new DefaultHttpContext
        {
            RequestAborted = cancellationSource.Token
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(0, problemDetailsService.WriteCallCount);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task Caller_requested_cancellation_from_the_Problem_Details_writer_is_rethrown_as_control_flow()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        using var cancellationSource = new CancellationTokenSource();
        var problemDetailsService = new ThrowingProblemDetailsService(
            new OperationCanceledException("writer-cancellation-sentinel"),
            _ => cancellationSource.Cancel());
        var writer = new CentralProblemDetailsWriter(problemDetailsService);
        var middleware = new CentralExceptionMiddleware(
            _ => Task.FromException(RequestValidationException.Single("$", "body_required", "A JSON request body is required.")),
            writer,
            loggerFactory.CreateLogger<CentralExceptionMiddleware>());
        var context = new DefaultHttpContext();
        var requestLifetime = new CapturingRequestLifetimeFeature
        {
            RequestAborted = cancellationSource.Token
        };
        context.Features.Set<IHttpRequestLifetimeFeature>(requestLifetime);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(1, problemDetailsService.WriteCallCount);
        Assert.False(requestLifetime.Aborted);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task Unmatched_routes_and_method_misses_remain_outside_defined_Problem_Details()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var unmatched = await client.GetAsync("/not-a-poc-route"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
            Assert.NotEqual("application/problem+json", unmatched.Content.Headers.ContentType?.MediaType);
        }

        using var methodMiss = await client.GetAsync(SetPath);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, methodMiss.StatusCode);
        Assert.NotEqual("application/problem+json", methodMiss.Content.Headers.ContentType?.MediaType);
    }

    private static async Task AssertNeverCreatedCurrentAsync(HttpClient client, string key)
    {
        using var current = await client.GetAsync($"{CurrentPath}?environment=UAT&key={Uri.EscapeDataString(key)}");
        await AssertProblemAsync(
            current,
            HttpStatusCode.NotFound,
            "configuration_not_found",
            "The requested Configuration Entry was not found.",
            CurrentPath);
    }

    private static async Task<ProblemResponse> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedDetail,
        string expectedPath,
        IReadOnlyList<(string Path, string Code, string Message)>? expectedErrors = null)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var expectedProperties = expectedErrors is null
            ? new[] { "type", "title", "status", "detail", "instance", "code", "traceId" }
            : new[] { "type", "title", "status", "detail", "instance", "code", "traceId", "errors" };

        AssertObjectProperties(root, expectedProperties);
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal(ReasonPhrases.GetReasonPhrase((int)expectedStatus), root.GetProperty("title").GetString());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedDetail, root.GetProperty("detail").GetString());
        Assert.Equal(expectedPath, root.GetProperty("instance").GetString());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        var traceId = root.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));

        if (expectedErrors is not null)
        {
            var errors = root.GetProperty("errors");
            Assert.Equal(JsonValueKind.Array, errors.ValueKind);
            Assert.Equal(expectedErrors.Count, errors.GetArrayLength());

            foreach (var expectedError in expectedErrors)
            {
                Assert.Contains(errors.EnumerateArray(), error =>
                    error.GetProperty("path").GetString() == expectedError.Path &&
                    error.GetProperty("code").GetString() == expectedError.Code &&
                    error.GetProperty("message").GetString() == expectedError.Message);
            }
        }
        else
        {
            Assert.False(root.TryGetProperty("errors", out _));
        }

        return new ProblemResponse(body, traceId!);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        CentralHostWebApplicationFactory sourceFactory,
        IConfigurationEntryStore store,
        CapturingLoggerProvider? loggerProvider) =>
        sourceFactory.WithWebHostBuilder(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Error);
                    logging.AddProvider(loggerProvider);
                });
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConfigurationEntryStore>();
                services.AddSingleton(store);
            });
        });

    private static WebApplicationFactory<Program> CreateLoggingFactory(
        CentralHostWebApplicationFactory sourceFactory,
        CapturingLoggerProvider loggerProvider) =>
        sourceFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Error);
                logging.AddProvider(loggerProvider);
            }));

    private static StringContent CreateJsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json) =>
        await client.PostAsync(path, CreateJsonContent(json));

    private static void AssertObjectProperties(JsonElement value, params string[] expectedProperties)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(
            expectedProperties.OrderBy(static property => property, StringComparer.Ordinal),
            value.EnumerateObject().Select(static property => property.Name).OrderBy(static property => property, StringComparer.Ordinal));
    }

    private static void AssertDoesNotContain(string actual, params string[] forbiddenValues)
    {
        foreach (var forbiddenValue in forbiddenValues)
        {
            Assert.DoesNotContain(forbiddenValue, actual, StringComparison.Ordinal);
        }
    }

    private static void AssertLogsDoNotContain(CapturingLoggerProvider loggerProvider, params string[] forbiddenValues)
    {
        var captured = string.Join(
            Environment.NewLine,
            loggerProvider.Entries.Select(entry => entry.ToSafeInspectionText()));
        AssertDoesNotContain(captured, forbiddenValues);
    }

    private static void AssertExactErrorLog(
        CapturedLogEntry actual,
        string expectedMessage,
        string expectedOriginalFormat,
        params (string Name, object? Value)[] expectedFields)
    {
        Assert.Equal(expectedMessage, actual.Message);
        Assert.Equal(expectedOriginalFormat, actual.GetStructuredValue("{OriginalFormat}")?.ToString());

        var expectedNames = expectedFields
            .Select(static field => field.Name)
            .Append("{OriginalFormat}")
            .OrderBy(static name => name, StringComparer.Ordinal);
        var actualNames = actual.StructuredValues
            .Select(static field => field.Key)
            .OrderBy(static name => name, StringComparer.Ordinal);
        Assert.Equal(expectedNames, actualNames);

        foreach (var expectedField in expectedFields)
        {
            Assert.Equal(expectedField.Value?.ToString(), actual.GetStructuredValue(expectedField.Name)?.ToString());
        }
    }

    private sealed record ProblemResponse(string Body, string TraceId);

    private sealed class ThrowingConfigurationEntryStore : IConfigurationEntryStore
    {
        private readonly Exception _exception;

        public ThrowingConfigurationEntryStore(Exception exception) => _exception = exception;

        public Task<ConfigurationCurrent?> GetCurrentAsync(
            ConfigurationEntryIdentity identity,
            CancellationToken cancellationToken) =>
            Task.FromResult<ConfigurationCurrent?>(null);

        public Task<ConfigurationHistory?> GetHistoryAsync(
            ConfigurationEntryIdentity identity,
            CancellationToken cancellationToken) =>
            Task.FromResult<ConfigurationHistory?>(null);

        public Task<IReadOnlyList<StoredResolution>> ResolveExistingAsync(
            EnvironmentName environment,
            IReadOnlyList<ConfigurationKey> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredResolution>>([]);

        public Task<ConfigurationCommandReceipt> SetAsync(
            ConfigurationEntryIdentity identity,
            string value,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromException<ConfigurationCommandReceipt>(_exception);

        public Task<RemoveStoreResult> RemoveAsync(
            ConfigurationEntryIdentity identity,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromException<RemoveStoreResult>(_exception);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose() { }
    }

    private sealed record CapturedLogEntry(
        string Category,
        LogLevel LogLevel,
        string Message,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> StructuredValues)
    {
        public object? GetStructuredValue(string name) =>
            StructuredValues.SingleOrDefault(pair => pair.Key == name).Value;

        public string ToSafeInspectionText() => string.Join(
            "|",
            [
                Category,
                LogLevel.ToString(),
                Message,
                Exception?.ToString() ?? string.Empty,
                .. StructuredValues.Select(pair => $"{pair.Key}={pair.Value}")
            ]);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<CapturedLogEntry> _entries;

        public CapturingLogger(string category, List<CapturedLogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

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
            var structuredValues = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToArray()
                : [];
            _entries.Add(new CapturedLogEntry(
                _category,
                logLevel,
                formatter(state, exception),
                exception,
                structuredValues));
        }
    }

    private sealed class RecordingProblemDetailsService : IProblemDetailsService
    {
        public int WriteCallCount { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            WriteCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingProblemDetailsService : IProblemDetailsService
    {
        private readonly Exception _exception;
        private readonly Action<ProblemDetailsContext>? _beforeThrow;

        public ThrowingProblemDetailsService(
            Exception exception,
            Action<ProblemDetailsContext>? beforeThrow = null)
        {
            _exception = exception;
            _beforeThrow = beforeThrow;
        }

        public int WriteCallCount { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            WriteCallCount++;
            _beforeThrow?.Invoke(context);
            return new ValueTask(Task.FromException(_exception));
        }
    }

    private sealed class MutableResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state) { }

        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state) { }

        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class CapturingRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }
}
