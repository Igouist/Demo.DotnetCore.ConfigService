using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.DemoHost.Tests.Diagnostics;

public sealed class StartupConsoleLoggerTests
{
    [Fact]
    public void Emits_exact_value_safe_marker_shapes()
    {
        using var output = new StringWriter();
        var logger = new StartupConsoleLogger(output);

        Emit(
            logger,
            LogLevel.Information,
            ("ValueCount", 2),
            ("MissingFallbackKeyCount", 1),
            ("AttemptCount", 2),
            ("ElapsedMs", 125d),
            ("Revision", "r1-0123456789abcdef01234567"),
            ("SecretValue", "never-serialize"));
        Emit(
            logger,
            LogLevel.Warning,
            ("AttemptCount", 1),
            ("FailureKind", "TransportFailure"),
            ("HttpStatusCode", null),
            ("DelayMs", 1000d),
            ("CentralErrorCode", null),
            ("CentralTraceId", null),
            ("Environment", "never-serialize"));
        Emit(
            logger,
            LogLevel.Warning,
            ("AttemptCount", 2),
            ("FailureKind", "HttpFailure"),
            ("HttpStatusCode", 500),
            ("DelayMs", 2000d),
            ("CentralErrorCode", "persistence_failure"),
            ("CentralTraceId", "0HN1RETRY:00000002"),
            ("Environment", "never-serialize"));
        Emit(
            logger,
            LogLevel.Error,
            ("FailureKind", "MissingRequiredKeys"),
            ("AttemptCount", 1),
            ("ElapsedMs", 20d),
            ("MissingRequiredKeys", new[] { "Demo:RequiredMessage" }),
            ("CentralErrorCode", "persistence_failure"),
            ("CentralTraceId", "0HN1ABCDEF:00000001"),
            ("Environment", "never-serialize"));

        string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        AssertMarker(
            lines[0],
            "loaded",
            "source", "event", "valueCount", "missingFallbackKeyCount", "attemptCount", "elapsedMs", "revision");
        using JsonDocument loaded = JsonDocument.Parse(lines[0]);
        Assert.Equal("r1-0123456789abcdef01234567", loaded.RootElement.GetProperty("revision").GetString());
        AssertMarker(
            lines[1],
            "retry",
            "source", "event", "attemptCount", "failureKind", "httpStatusCode", "delayMs",
            "centralErrorCode", "centralTraceId");
        AssertMarker(
            lines[2],
            "retry",
            "source", "event", "attemptCount", "failureKind", "httpStatusCode", "delayMs",
            "centralErrorCode", "centralTraceId");
        using JsonDocument retryWithoutProblem = JsonDocument.Parse(lines[1]);
        Assert.Equal(JsonValueKind.Null, retryWithoutProblem.RootElement.GetProperty("centralErrorCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, retryWithoutProblem.RootElement.GetProperty("centralTraceId").ValueKind);
        using JsonDocument retryWithProblem = JsonDocument.Parse(lines[2]);
        Assert.Equal(500, retryWithProblem.RootElement.GetProperty("httpStatusCode").GetInt32());
        Assert.Equal("persistence_failure", retryWithProblem.RootElement.GetProperty("centralErrorCode").GetString());
        Assert.Equal("0HN1RETRY:00000002", retryWithProblem.RootElement.GetProperty("centralTraceId").GetString());
        AssertMarker(
            lines[3],
            "failed",
            "source", "event", "failureKind", "attemptCount", "httpStatusCode", "elapsedMs", "missingRequiredKeys",
            "centralErrorCode", "centralTraceId");
        using JsonDocument failed = JsonDocument.Parse(lines[3]);
        Assert.Equal(
            ["Demo:RequiredMessage"],
            failed.RootElement.GetProperty("missingRequiredKeys").EnumerateArray().Select(key => key.GetString()));
        Assert.Equal("persistence_failure", failed.RootElement.GetProperty("centralErrorCode").GetString());
        Assert.Equal("0HN1ABCDEF:00000001", failed.RootElement.GetProperty("centralTraceId").GetString());
        Assert.DoesNotContain("never-serialize", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Emits_unknown_response_member_marker_with_names_only()
    {
        using var output = new StringWriter();
        var logger = new StartupConsoleLogger(output);

        Emit(
            logger,
            LogLevel.Warning,
            ("UnknownResponseMembers", new[] { "future", "other" }),
            ("Environment", "never-serialize"));

        string line = Assert.Single(
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        AssertMarker(line, "unknown-response-members", "source", "event", "unknownResponseMembers");
        using JsonDocument marker = JsonDocument.Parse(line);
        Assert.Equal(
            ["future", "other"],
            marker.RootElement.GetProperty("unknownResponseMembers").EnumerateArray().Select(name => name.GetString()));
        Assert.DoesNotContain("never-serialize", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_unrecognized_levels_without_calling_the_formatter()
    {
        using var output = new StringWriter();
        var logger = new StartupConsoleLogger(output);
        bool formatterCalled = false;

        logger.Log(
            LogLevel.Debug,
            default,
            new StructuredState(("Secret", "never-format")),
            new InvalidOperationException("never-serialize"),
            (_, _) =>
            {
                formatterCalled = true;
                return "never-format";
            });

        Assert.False(formatterCalled);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Ignores_unknown_structured_states_at_recognized_levels_without_calling_the_formatter()
    {
        using var output = new StringWriter();
        var logger = new StartupConsoleLogger(output);
        bool formatterCalled = false;

        logger.Log(
            LogLevel.Information,
            default,
            new StructuredState(("Secret", "never-format")),
            new InvalidOperationException("never-serialize"),
            (_, _) =>
            {
                formatterCalled = true;
                return "never-format";
            });

        Assert.False(formatterCalled);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Ignores_malformed_structured_states_at_recognized_levels_without_calling_the_formatter()
    {
        using var output = new StringWriter();
        var logger = new StartupConsoleLogger(output);
        bool formatterCalled = false;

        logger.Log(
            LogLevel.Information,
            default,
            new StructuredState(
                ("ValueCount", "not-an-int"),
                ("MissingFallbackKeyCount", 1),
                ("AttemptCount", 2),
                ("ElapsedMs", 125d)),
            new InvalidOperationException("never-serialize"),
            (_, _) =>
            {
                formatterCalled = true;
                return "never-format";
            });

        Assert.False(formatterCalled);
        Assert.Equal(string.Empty, output.ToString());
    }

    private static void Emit(
        StartupConsoleLogger logger,
        LogLevel level,
        params (string Name, object? Value)[] fields) =>
        logger.Log(
            level,
            default,
            new StructuredState(fields),
            new InvalidOperationException("never-serialize"),
            static (_, _) => "never-format");

    private static void AssertMarker(
        string json,
        string expectedEvent,
        params string[] expectedProperties)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("demo-central-config", root.GetProperty("source").GetString());
        Assert.Equal(expectedEvent, root.GetProperty("event").GetString());
        Assert.Equal(
            expectedProperties.Order(StringComparer.Ordinal),
            root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
    }

    private sealed class StructuredState(params (string Name, object? Value)[] fields)
        : List<KeyValuePair<string, object?>>(
            fields.Select(static field => new KeyValuePair<string, object?>(field.Name, field.Value)));
}
