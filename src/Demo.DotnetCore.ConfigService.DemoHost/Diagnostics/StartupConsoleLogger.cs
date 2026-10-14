using System.Net;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.Client;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.DemoHost;

internal sealed class StartupConsoleLogger : ILogger
{
    private const string Source = "demo-central-config";
    private readonly object _gate = new();
    private readonly TextWriter _output;

    internal StartupConsoleLogger(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel is LogLevel.Information or LogLevel.Warning or LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)
            || state is not IEnumerable<KeyValuePair<string, object?>> structured)
        {
            return;
        }

        if (!TryReadFields(structured, out Dictionary<string, object?> fields)
            || !TryCreateMarker(logLevel, fields, exception, out object? marker))
        {
            return;
        }

        string line = JsonSerializer.Serialize(marker);

        lock (_gate)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }

    private static bool TryReadFields(
        IEnumerable<KeyValuePair<string, object?>> structured,
        out Dictionary<string, object?> fields)
    {
        fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, object?> field in structured)
        {
            if (string.IsNullOrEmpty(field.Key) || !fields.TryAdd(field.Key, field.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateMarker(
        LogLevel logLevel,
        IReadOnlyDictionary<string, object?> fields,
        Exception? exception,
        out object? marker)
    {
        marker = null;

        switch (logLevel)
        {
            case LogLevel.Information:
                if (!TryReadInt32(fields, "ValueCount", out int valueCount)
                    || !TryReadInt32(fields, "MissingFallbackKeyCount", out int missingFallbackKeyCount)
                    || !TryReadInt32(fields, "AttemptCount", out int informationAttemptCount)
                    || !TryReadDouble(fields, "ElapsedMs", out double informationElapsedMs))
                {
                    return false;
                }

                marker = new
                {
                    source = Source,
                    @event = "loaded",
                    valueCount,
                    missingFallbackKeyCount,
                    attemptCount = informationAttemptCount,
                    elapsedMs = informationElapsedMs
                };
                return true;
            case LogLevel.Warning:
                if (!TryReadInt32(fields, "AttemptCount", out int warningAttemptCount)
                    || !TryReadText(fields, "FailureKind", out string warningFailureKind)
                    || !TryReadNullableInt32(fields, "HttpStatusCode", out int? warningHttpStatusCode)
                    || !TryReadDouble(fields, "DelayMs", out double delayMs))
                {
                    return false;
                }

                marker = new
                {
                    source = Source,
                    @event = "retry",
                    attemptCount = warningAttemptCount,
                    failureKind = warningFailureKind,
                    httpStatusCode = warningHttpStatusCode,
                    delayMs
                };
                return true;
            case LogLevel.Error:
                if (!TryReadText(fields, "FailureKind", out string errorFailureKind)
                    || !TryReadInt32(fields, "AttemptCount", out int errorAttemptCount)
                    || !TryReadDouble(fields, "ElapsedMs", out double errorElapsedMs))
                {
                    return false;
                }

                marker = new
                {
                    source = Source,
                    @event = "failed",
                    failureKind = errorFailureKind,
                    attemptCount = errorAttemptCount,
                    httpStatusCode = exception is CentralConfigLoadException
                    {
                        HttpStatusCode: HttpStatusCode statusCode
                    }
                        ? (int?)statusCode
                        : null,
                    elapsedMs = errorElapsedMs
                };
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadInt32(
        IReadOnlyDictionary<string, object?> fields,
        string name,
        out int value)
    {
        if (fields.TryGetValue(name, out object? rawValue) && rawValue is int intValue)
        {
            value = intValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadNullableInt32(
        IReadOnlyDictionary<string, object?> fields,
        string name,
        out int? value)
    {
        if (!fields.TryGetValue(name, out object? rawValue))
        {
            value = default;
            return false;
        }

        if (rawValue is null)
        {
            value = null;
            return true;
        }

        if (rawValue is int intValue)
        {
            value = intValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadDouble(
        IReadOnlyDictionary<string, object?> fields,
        string name,
        out double value)
    {
        if (fields.TryGetValue(name, out object? rawValue) && rawValue is double doubleValue)
        {
            value = doubleValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadText(
        IReadOnlyDictionary<string, object?> fields,
        string name,
        out string value)
    {
        if (!fields.TryGetValue(name, out object? rawValue))
        {
            value = string.Empty;
            return false;
        }

        if (rawValue is string textValue && !string.IsNullOrEmpty(textValue))
        {
            value = textValue;
            return true;
        }

        if (rawValue is CentralConfigFailureKind failureKind)
        {
            value = failureKind.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed class NoopScope : IDisposable
    {
        internal static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
