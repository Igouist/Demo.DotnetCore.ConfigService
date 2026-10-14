using System.Net;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class RetryingDistributionResolver
{
    internal static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];
    private const int MaxAttempts = 4;

    private readonly IDistributionTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly IRetryJitter _jitter;
    private readonly StartupLogEmitter _logEmitter;
    private readonly IDistributionResponseParser _responseParser;

    internal RetryingDistributionResolver(
        IDistributionTransport transport,
        TimeProvider timeProvider,
        IRetryJitter jitter,
        StartupLogEmitter logEmitter,
        IDistributionResponseParser responseParser)
    {
        _transport = transport;
        _timeProvider = timeProvider;
        _jitter = jitter;
        _logEmitter = logEmitter;
        _responseParser = responseParser;
    }

    internal async Task<ResolvedCentralConfig> ResolveAsync(
        EnabledCentralConfigBootstrap bootstrap,
        long startedTimestamp,
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken)
    {
        int completedAttempts = 0;
        var request = new DistributionResolveRequest(
            bootstrap.Environment,
            bootstrap.KeySelection.RequestKeys);

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ThrowIfPreempted(
                callerToken,
                totalTimeoutToken,
                completedAttempts,
                startedTimestamp,
                bootstrap.Environment);

            AttemptArbitration arbitration;
            using (var attemptTimeout = new CancellationTokenSource(AttemptTimeout, _timeProvider))
            using (var arbiter = new AttemptArbiter(
                       callerToken,
                       totalTimeoutToken,
                       attemptTimeout.Token))
            {
                arbitration = await arbiter.Start(cancellationToken =>
                    _transport.ResolveAsync(
                        bootstrap.ResolveEndpoint,
                        request,
                        cancellationToken));
            }

            if (arbitration.TransportStarted)
            {
                completedAttempts = attempt;
            }

            CentralConfigFailureKind? attemptFailure = null;
            DistributionHttpResponse? response = null;
            if (!arbitration.TransportCompleted)
            {
                attemptFailure = GetAttemptCancellationKind(
                    arbitration.Cancellation,
                    callerToken,
                    completedAttempts,
                    startedTimestamp,
                    bootstrap.Environment);
            }
            else
            {
                try
                {
                    response = await arbitration.CompletedTransportTask!;
                    if (response is null)
                    {
                        attemptFailure = CentralConfigFailureKind.TransportFailure;
                    }
                }
                catch (Exception)
                {
                    attemptFailure = CentralConfigFailureKind.TransportFailure;
                }
            }

            if (attemptFailure is { } failureKind)
            {
                if (attempt == MaxAttempts)
                {
                    throw Failure(
                        failureKind,
                        completedAttempts,
                        startedTimestamp,
                        bootstrap.Environment);
                }

                await WaitForRetryAsync(
                    attempt,
                    failureKind,
                    null,
                    completedAttempts,
                    startedTimestamp,
                    bootstrap.Environment,
                    callerToken,
                    totalTimeoutToken);
                continue;
            }

            DistributionHttpResponse completedResponse = response!;
            if (completedResponse.StatusCode == HttpStatusCode.OK)
            {
                return await ParseSuccessfulResponseAsync(
                    completedResponse,
                    bootstrap,
                    completedAttempts,
                    startedTimestamp,
                    callerToken,
                    totalTimeoutToken);
            }

            if (completedResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw Failure(
                    CentralConfigFailureKind.Unauthorized,
                    completedAttempts,
                    startedTimestamp,
                    bootstrap.Environment,
                    completedResponse.StatusCode);
            }

            if (!IsRetryable(completedResponse.StatusCode) || attempt == MaxAttempts)
            {
                throw Failure(
                    CentralConfigFailureKind.HttpFailure,
                    completedAttempts,
                    startedTimestamp,
                    bootstrap.Environment,
                    completedResponse.StatusCode);
            }

            await WaitForRetryAsync(
                attempt,
                CentralConfigFailureKind.HttpFailure,
                completedResponse.StatusCode,
                completedAttempts,
                startedTimestamp,
                bootstrap.Environment,
                callerToken,
                totalTimeoutToken);
        }

        throw Failure(
            CentralConfigFailureKind.TransportFailure,
            completedAttempts,
            startedTimestamp,
            bootstrap.Environment);
    }

    private async Task<ResolvedCentralConfig> ParseSuccessfulResponseAsync(
        DistributionHttpResponse response,
        EnabledCentralConfigBootstrap bootstrap,
        int attemptCount,
        long startedTimestamp,
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken)
    {
        ThrowIfPreempted(
            callerToken,
            totalTimeoutToken,
            attemptCount,
            startedTimestamp,
            bootstrap.Environment);
        Task<ResolvedCentralConfig> validationTask = Task.Run(() =>
            ParseSuccessfulResponse(
                response,
                bootstrap,
                attemptCount,
                startedTimestamp));
        ObserveFault(validationTask);
        using var validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            totalTimeoutToken);

        try
        {
            return await validationTask.WaitAsync(validationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerToken);
            }

            if (totalTimeoutToken.IsCancellationRequested)
            {
                throw Failure(
                    CentralConfigFailureKind.TotalTimeout,
                    attemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
            }

            throw;
        }
    }

    private ResolvedCentralConfig ParseSuccessfulResponse(
        DistributionHttpResponse response,
        EnabledCentralConfigBootstrap bootstrap,
        int attemptCount,
        long startedTimestamp)
    {
        ValidatedDistributionResponse? validated;
        try
        {
            validated = _responseParser.ParseAndValidate(
                response.Body,
                bootstrap.KeySelection);
        }
        catch (Exception)
        {
            validated = null;
        }

        if (validated is null)
        {
            throw Failure(
                CentralConfigFailureKind.InvalidResponse,
                attemptCount,
                startedTimestamp,
                bootstrap.Environment,
                HttpStatusCode.OK);
        }

        string[] missingRequired = bootstrap.KeySelection.RequiredKeys
            .Where(key => validated.IsMissing(key.DeclaredKey))
            .Select(key => key.DeclaredKey)
            .ToArray();
        if (missingRequired.Length != 0)
        {
            throw Failure(
                CentralConfigFailureKind.MissingRequiredKeys,
                attemptCount,
                startedTimestamp,
                bootstrap.Environment,
                HttpStatusCode.OK,
                missingRequired);
        }

        int missingFallbackCount = bootstrap.KeySelection.FallbackKeys.Count(key =>
            validated.IsMissing(key.DeclaredKey));
        return new ResolvedCentralConfig(
            validated.Values,
            missingFallbackCount,
            attemptCount);
    }

    private async Task WaitForRetryAsync(
        int completedAttempt,
        CentralConfigFailureKind failureKind,
        HttpStatusCode? statusCode,
        int attemptCount,
        long startedTimestamp,
        string environment,
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken)
    {
        RetryScheduleDecision scheduleDecision;
        using (var scheduleArbiter = new RetryScheduleArbiter(
                   callerToken,
                   totalTimeoutToken))
        {
            scheduleDecision = scheduleArbiter.TrySchedule();
        }

        if (!scheduleDecision.IsScheduled)
        {
            ThrowRetryCancellation(
                scheduleDecision.Cancellation,
                callerToken,
                attemptCount,
                startedTimestamp,
                environment);
        }

        TimeSpan delay = RetryDelays[completedAttempt - 1] + _jitter.Next();
        using var linkedDelay = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            totalTimeoutToken);

        try
        {
            Task scheduledDelay = Task.Delay(delay, _timeProvider, linkedDelay.Token);
            _logEmitter.TryWarning(completedAttempt, failureKind, statusCode, delay);
            await scheduledDelay;
        }
        catch (OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerToken);
            }

            if (totalTimeoutToken.IsCancellationRequested)
            {
                throw Failure(
                    CentralConfigFailureKind.TotalTimeout,
                    attemptCount,
                    startedTimestamp,
                    environment);
            }

            throw Failure(
                CentralConfigFailureKind.TransportFailure,
                attemptCount,
                startedTimestamp,
                environment);
        }
    }

    private CentralConfigFailureKind GetAttemptCancellationKind(
        OwnedCancellationSnapshot cancellation,
        CancellationToken callerToken,
        int attemptCount,
        long startedTimestamp,
        string environment)
    {
        if (cancellation.HighestPriority == OwnedCancellationKind.Caller)
        {
            throw new OperationCanceledException(callerToken);
        }

        if (cancellation.HighestPriority == OwnedCancellationKind.TotalTimeout)
        {
            throw Failure(
                CentralConfigFailureKind.TotalTimeout,
                attemptCount,
                startedTimestamp,
                environment);
        }

        return cancellation.HighestPriority == OwnedCancellationKind.AttemptTimeout
            ? CentralConfigFailureKind.AttemptTimeout
            : CentralConfigFailureKind.TransportFailure;
    }

    private void ThrowRetryCancellation(
        OwnedCancellationSnapshot cancellation,
        CancellationToken callerToken,
        int attemptCount,
        long startedTimestamp,
        string environment)
    {
        if (cancellation.HighestPriority == OwnedCancellationKind.Caller)
        {
            throw new OperationCanceledException(callerToken);
        }

        CentralConfigFailureKind failureKind =
            cancellation.HighestPriority == OwnedCancellationKind.TotalTimeout
                ? CentralConfigFailureKind.TotalTimeout
                : CentralConfigFailureKind.TransportFailure;
        throw Failure(
            failureKind,
            attemptCount,
            startedTimestamp,
            environment);
    }

    private void ThrowIfPreempted(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        int attemptCount,
        long startedTimestamp,
        string environment)
    {
        if (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken);
        }

        if (totalTimeoutToken.IsCancellationRequested)
        {
            throw Failure(
                CentralConfigFailureKind.TotalTimeout,
                attemptCount,
                startedTimestamp,
                environment);
        }
    }

    private CentralConfigLoadException Failure(
        CentralConfigFailureKind kind,
        int attemptCount,
        long startedTimestamp,
        string environment,
        HttpStatusCode? statusCode = null,
        IEnumerable<string>? missingRequiredKeys = null) =>
        new(
            kind,
            attemptCount,
            _timeProvider.GetElapsedTime(startedTimestamp),
            statusCode,
            environment,
            missingRequiredKeys);

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;
}
