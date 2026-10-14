using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

internal sealed class RecordingLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private readonly SemaphoreSlim _entryAdded = new(0);

    internal IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    internal async Task WaitForEntryCountAsync(int expectedCount)
    {
        while (Entries.Count < expectedCount)
        {
            await _entryAdded.WaitAsync();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        IReadOnlyList<KeyValuePair<string, object?>> fields =
            state is IReadOnlyList<KeyValuePair<string, object?>> structured
                ? structured
                : Array.Empty<KeyValuePair<string, object?>>();
        string originalFormat = fields
            .FirstOrDefault(field => field.Key == "{OriginalFormat}")
            .Value?.ToString() ?? string.Empty;
        var entry = new LogEntry(logLevel, exception, formatter(state, exception), originalFormat, fields);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        _entryAdded.Release();
    }
}

internal sealed record LogEntry(
    LogLevel Level,
    Exception? Exception,
    string Message,
    string OriginalFormat,
    IReadOnlyList<KeyValuePair<string, object?>> Fields);
