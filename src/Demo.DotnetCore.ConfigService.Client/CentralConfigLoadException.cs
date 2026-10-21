using System.Collections.ObjectModel;
using System.Net;

namespace Demo.DotnetCore.ConfigService.Client;

public sealed class CentralConfigLoadException : Exception
{
    internal CentralConfigLoadException(
        CentralConfigFailureKind failureKind,
        int attemptCount,
        TimeSpan elapsed,
        HttpStatusCode? httpStatusCode,
        string? environment,
        IEnumerable<string>? missingRequiredKeys = null,
        CentralProblemReference? centralProblem = null)
        : base(GetSafeMessage(failureKind))
    {
        FailureKind = failureKind;
        AttemptCount = attemptCount;
        Elapsed = elapsed;
        HttpStatusCode = httpStatusCode;
        Environment = environment;
        CentralErrorCode = centralProblem?.Code;
        CentralTraceId = centralProblem?.TraceId;

        string[] keys = failureKind == CentralConfigFailureKind.MissingRequiredKeys
            ? missingRequiredKeys?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? []
            : [];
        MissingRequiredKeys = new ReadOnlyCollection<string>(keys);
    }

    public CentralConfigFailureKind FailureKind { get; }

    public int AttemptCount { get; }

    public TimeSpan Elapsed { get; }

    public HttpStatusCode? HttpStatusCode { get; }

    public string? Environment { get; }

    public IReadOnlyList<string> MissingRequiredKeys { get; }

    // From the final Central Problem Details response; null when no validated value was returned.
    public string? CentralErrorCode { get; }

    // Correlates this failure with the Central Host log entry for the final attempt.
    public string? CentralTraceId { get; }

    private static string GetSafeMessage(CentralConfigFailureKind failureKind) => failureKind switch
    {
        CentralConfigFailureKind.InvalidBootstrapConfiguration => "Central configuration bootstrap is invalid.",
        CentralConfigFailureKind.InvalidConfigurationPipeline => "The configuration pipeline cannot accept the central snapshot.",
        CentralConfigFailureKind.TransportFailure => "Central configuration could not be reached.",
        CentralConfigFailureKind.AttemptTimeout => "A central configuration request timed out.",
        CentralConfigFailureKind.TotalTimeout => "Central configuration exceeded its startup time budget.",
        CentralConfigFailureKind.Unauthorized => "Central configuration access was not authorized.",
        CentralConfigFailureKind.HttpFailure => "Central configuration returned an unsuccessful HTTP status.",
        CentralConfigFailureKind.InvalidResponse => "Central configuration returned an invalid response.",
        CentralConfigFailureKind.MissingRequiredKeys => "Central configuration is missing required keys.",
        _ => "Central configuration loading failed."
    };
}
