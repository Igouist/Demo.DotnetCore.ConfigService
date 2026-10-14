using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

internal sealed class ThrowingLogger(bool throwFromIsEnabled) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => throwFromIsEnabled
        ? throw new InvalidOperationException("logger failure")
        : true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        throw new InvalidOperationException("logger failure");
}
