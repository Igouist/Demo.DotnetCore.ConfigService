using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class CentralConfigRegistration
{
    private readonly IDistributionTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly IRetryJitter _jitter;
    private readonly IDistributionResponseParser _responseParser;
    private readonly ISnapshotSourcePreparer _snapshotSourcePreparer;
    private readonly ISnapshotCommitter _snapshotCommitter;

    internal CentralConfigRegistration(
        IDistributionTransport transport,
        TimeProvider timeProvider,
        IRetryJitter jitter,
        IDistributionResponseParser responseParser,
        ISnapshotSourcePreparer snapshotSourcePreparer,
        ISnapshotCommitter snapshotCommitter)
    {
        _transport = transport;
        _timeProvider = timeProvider;
        _jitter = jitter;
        _responseParser = responseParser;
        _snapshotSourcePreparer = snapshotSourcePreparer;
        _snapshotCommitter = snapshotCommitter;
    }

    internal async Task RegisterAsync(
        IHostApplicationBuilder builder,
        ILogger? startupLogger,
        CancellationToken callerToken)
    {
        long startedTimestamp = _timeProvider.GetTimestamp();
        using var totalTimeout = new CancellationTokenSource(
            RetryingDistributionResolver.TotalTimeout,
            _timeProvider);
        var logEmitter = new StartupLogEmitter(startupLogger);

        try
        {
            if (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerToken);
            }

            BootstrapReadResult bootstrapResult;
            try
            {
                bootstrapResult = CentralConfigBootstrapReader.Read(builder.Configuration);
            }
            catch (Exception)
            {
                throw Failure(
                    CentralConfigFailureKind.InvalidBootstrapConfiguration,
                    0,
                    startedTimestamp,
                    null);
            }

            if (!bootstrapResult.IsValid)
            {
                throw Failure(
                    CentralConfigFailureKind.InvalidBootstrapConfiguration,
                    0,
                    startedTimestamp,
                    null);
            }

            if (!bootstrapResult.IsEnabled)
            {
                return;
            }

            EnabledCentralConfigBootstrap bootstrap = bootstrapResult.EnabledBootstrap!;
            if (!EnvironmentJsonSourceAnchor.TryFindIndex(
                    builder.Configuration.Sources,
                    builder.Environment.EnvironmentName,
                    out _))
            {
                throw Failure(
                    CentralConfigFailureKind.InvalidConfigurationPipeline,
                    0,
                    startedTimestamp,
                    bootstrap.Environment);
            }

            var resolver = new RetryingDistributionResolver(
                _transport,
                _timeProvider,
                _jitter,
                logEmitter,
                _responseParser);
            ResolvedCentralConfig resolved = await resolver.ResolveAsync(
                bootstrap,
                startedTimestamp,
                callerToken,
                totalTimeout.Token);

            CentralConfigSnapshotSource snapshotSource;
            try
            {
                snapshotSource = await PrepareSnapshotSourceAsync(
                    resolved.Values,
                    callerToken,
                    totalTimeout.Token,
                    resolved.AttemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
            }
            catch (CentralConfigLoadException)
            {
                throw;
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerToken);
            }
            catch (Exception)
            {
                throw Failure(
                    CentralConfigFailureKind.InvalidConfigurationPipeline,
                    resolved.AttemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
            }

            ThrowIfPreempted(
                callerToken,
                totalTimeout.Token,
                resolved.AttemptCount,
                startedTimestamp,
                bootstrap.Environment);
            bool hasAnchor = EnvironmentJsonSourceAnchor.TryFindIndex(
                    builder.Configuration.Sources,
                    builder.Environment.EnvironmentName,
                    out int anchorIndex);
            ThrowIfPreempted(
                callerToken,
                totalTimeout.Token,
                resolved.AttemptCount,
                startedTimestamp,
                bootstrap.Environment);
            if (!hasAnchor)
            {
                throw Failure(
                    CentralConfigFailureKind.InvalidConfigurationPipeline,
                    resolved.AttemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
            }

            SnapshotCommitDecision commitDecision;
            try
            {
                commitDecision = _snapshotCommitter.TryCommit(
                    callerToken,
                    totalTimeout.Token,
                    () => builder.Configuration.Sources.Insert(
                        anchorIndex + 1,
                        snapshotSource));
            }
            catch (Exception)
            {
                throw Failure(
                    CentralConfigFailureKind.InvalidConfigurationPipeline,
                    resolved.AttemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
            }

            if (!commitDecision.IsCommitted)
            {
                ThrowCancellation(
                    commitDecision.Cancellation,
                    callerToken,
                    resolved.AttemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
                throw Failure(
                    CentralConfigFailureKind.InvalidConfigurationPipeline,
                    resolved.AttemptCount,
                    startedTimestamp,
                    bootstrap.Environment);
            }

            logEmitter.TryInformation(
                resolved.Values.Count,
                resolved.MissingFallbackKeyCount,
                resolved.AttemptCount,
                _timeProvider.GetElapsedTime(startedTimestamp),
                resolved.Revision);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerToken);
        }
        catch (CentralConfigLoadException exception)
        {
            logEmitter.TryError(exception);
            throw;
        }
    }

    private CentralConfigLoadException Failure(
        CentralConfigFailureKind kind,
        int attemptCount,
        long startedTimestamp,
        string? environment) =>
        new(
            kind,
            attemptCount,
            _timeProvider.GetElapsedTime(startedTimestamp),
            null,
            environment);

    private async Task<CentralConfigSnapshotSource> PrepareSnapshotSourceAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        int attemptCount,
        long startedTimestamp,
        string environment)
    {
        ThrowIfPreempted(
            callerToken,
            totalTimeoutToken,
            attemptCount,
            startedTimestamp,
            environment);
        Task<CentralConfigSnapshotSource> preparationTask = Task.Run(() =>
            _snapshotSourcePreparer.Prepare(values));
        ObserveFault(preparationTask);
        using var preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            totalTimeoutToken);

        try
        {
            return await preparationTask.WaitAsync(preparationCancellation.Token);
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

            throw;
        }
    }

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

    private void ThrowIfPreempted(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        int attemptCount,
        long startedTimestamp,
        string environment)
    {
        ThrowCancellation(
            OwnedCancellationSnapshot.Capture(
                callerToken,
                totalTimeoutToken,
                CancellationToken.None),
            callerToken,
            attemptCount,
            startedTimestamp,
            environment);
    }

    private void ThrowCancellation(
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
    }
}
