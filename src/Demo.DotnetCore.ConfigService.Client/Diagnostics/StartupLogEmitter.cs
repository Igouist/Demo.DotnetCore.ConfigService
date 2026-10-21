using System.Net;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class StartupLogEmitter(ILogger? logger)
{
    internal void TryInformation(
        int valueCount,
        int missingFallbackKeyCount,
        int attemptCount,
        TimeSpan elapsed) =>
        TryEmit(
            LogLevel.Information,
            target => target.LogInformation(
                "Central configuration loaded: {ValueCount} values, {MissingFallbackKeyCount} fallback-missing keys, {AttemptCount} attempt(s), {ElapsedMs} ms.",
                valueCount,
                missingFallbackKeyCount,
                attemptCount,
                elapsed.TotalMilliseconds));

    internal void TryWarning(
        int attemptCount,
        CentralConfigFailureKind failureKind,
        HttpStatusCode? httpStatusCode,
        TimeSpan delay,
        CentralProblemReference? centralProblem = null) =>
        TryEmit(
            LogLevel.Warning,
            target => target.LogWarning(
                "Central configuration retry scheduled after attempt {AttemptCount}: {FailureKind}; HTTP status {HttpStatusCode}; delay {DelayMs} ms; central error code {CentralErrorCode}; central trace id {CentralTraceId}.",
                attemptCount,
                failureKind,
                httpStatusCode is null ? null : (int)httpStatusCode.Value,
                delay.TotalMilliseconds,
                centralProblem?.Code,
                centralProblem?.TraceId));

    internal void TryError(CentralConfigLoadException exception) =>
        TryEmit(
            LogLevel.Error,
            target => target.LogError(
                exception,
                "Central configuration load failed: {FailureKind}; attempts {AttemptCount}; elapsed {ElapsedMs} ms; Environment {Environment}; missing required keys {MissingRequiredKeys}; central error code {CentralErrorCode}; central trace id {CentralTraceId}.",
                exception.FailureKind,
                exception.AttemptCount,
                exception.Elapsed.TotalMilliseconds,
                exception.Environment,
                exception.MissingRequiredKeys,
                exception.CentralErrorCode,
                exception.CentralTraceId));

    private void TryEmit(LogLevel level, Action<ILogger> emit)
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            if (logger.IsEnabled(level))
            {
                emit(logger);
            }
        }
        catch (Exception)
        {
            // POC-046: optional diagnostics never change the primary loading result.
        }
    }
}
