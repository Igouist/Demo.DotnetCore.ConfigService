namespace Demo.DotnetCore.ConfigService.Client;

internal enum OwnedCancellationKind
{
    None,
    Caller,
    TotalTimeout,
    AttemptTimeout
}

internal readonly record struct OwnedCancellationSnapshot(
    bool CallerRequested,
    bool TotalTimeoutRequested,
    bool AttemptTimeoutRequested)
{
    internal bool IsCancellationRequested =>
        CallerRequested || TotalTimeoutRequested || AttemptTimeoutRequested;

    // POC-047 fixes Caller -> TotalTimeout -> AttemptTimeout priority to preserve the original Caller token and prevent concurrent signals from being misreported as Caller cancellation.
    internal OwnedCancellationKind HighestPriority =>
        CallerRequested
            ? OwnedCancellationKind.Caller
            : TotalTimeoutRequested
                ? OwnedCancellationKind.TotalTimeout
                : AttemptTimeoutRequested
                    ? OwnedCancellationKind.AttemptTimeout
                    : OwnedCancellationKind.None;

    internal static OwnedCancellationSnapshot Capture(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        CancellationToken attemptTimeoutToken) =>
        new(
            callerToken.IsCancellationRequested,
            totalTimeoutToken.IsCancellationRequested,
            attemptTimeoutToken.IsCancellationRequested);

    internal OwnedCancellationSnapshot Merge(OwnedCancellationSnapshot other) =>
        new(
            CallerRequested || other.CallerRequested,
            TotalTimeoutRequested || other.TotalTimeoutRequested,
            AttemptTimeoutRequested || other.AttemptTimeoutRequested);
}

internal readonly record struct AttemptArbitration(
    Task<DistributionHttpResponse>? CompletedTransportTask,
    bool TransportStarted,
    OwnedCancellationSnapshot Cancellation)
{
    internal bool TransportCompleted => CompletedTransportTask is not null;
}

internal sealed class AttemptArbiter : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _totalTimeoutToken;
    private readonly CancellationToken _attemptTimeoutToken;
    private readonly Action<
        Task<DistributionHttpResponse>,
        Action<Task<DistributionHttpResponse>>> _observeCompletion;
    private readonly Action _beforeCancellationObservation;
    private CancellationTokenSource? _transportCancellation = new();
    private readonly TaskCompletionSource<AttemptArbitration> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly CancellationTokenRegistration _totalTimeoutRegistration;
    private readonly CancellationTokenRegistration _attemptTimeoutRegistration;

    private bool _transportStarted;
    private bool _cancellationClaimed;
    private bool _cancellationFinalizationStarted;
    private OwnedCancellationSnapshot _claimedCancellation;
    private bool _latched;

    internal AttemptArbiter(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        CancellationToken attemptTimeoutToken)
        : this(
            callerToken,
            totalTimeoutToken,
            attemptTimeoutToken,
            ObserveCompletionWithTaskScheduler,
            static () => { })
    {
    }

    internal AttemptArbiter(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        CancellationToken attemptTimeoutToken,
        Action<Task<DistributionHttpResponse>, Action<Task<DistributionHttpResponse>>> observeCompletion,
        Action beforeCancellationObservation)
    {
        ArgumentNullException.ThrowIfNull(observeCompletion);
        ArgumentNullException.ThrowIfNull(beforeCancellationObservation);
        _callerToken = callerToken;
        _totalTimeoutToken = totalTimeoutToken;
        _attemptTimeoutToken = attemptTimeoutToken;
        _observeCompletion = observeCompletion;
        _beforeCancellationObservation = beforeCancellationObservation;
        _callerRegistration = callerToken.UnsafeRegister(
            static state => ((AttemptArbiter)state!).OnCancellation(),
            this);
        _totalTimeoutRegistration = totalTimeoutToken.UnsafeRegister(
            static state => ((AttemptArbiter)state!).OnCancellation(),
            this);
        _attemptTimeoutRegistration = attemptTimeoutToken.UnsafeRegister(
            static state => ((AttemptArbiter)state!).OnCancellation(),
            this);
    }

    internal Task<AttemptArbitration> Start(
        Func<CancellationToken, Task<DistributionHttpResponse>> startTransport)
    {
        ArgumentNullException.ThrowIfNull(startTransport);
        AttemptArbitration? decision = null;
        TransportStartWork? startWork = null;
        lock (_gate)
        {
            if (_latched)
            {
                return _completion.Task;
            }

            OwnedCancellationSnapshot snapshot = CaptureCancellation();
            if (snapshot.IsCancellationRequested)
            {
                _cancellationClaimed = true;
                _claimedCancellation = _claimedCancellation.Merge(snapshot);
                decision = LatchCancellation(_claimedCancellation);
            }
            else
            {
                _transportStarted = true;
                var proxy = new TaskCompletionSource<DistributionHttpResponse>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startWork = new TransportStartWork(
                    startTransport,
                    _transportCancellation!.Token,
                    proxy);
            }
        }

        if (decision is { } completedDecision)
        {
            _completion.TrySetResult(completedDecision);
        }

        if (startWork is not null)
        {
            ObserveCompletion(startWork.ProxyTask);
            QueueTransportStart(startWork);
        }

        return _completion.Task;
    }

    public void Dispose()
    {
        _callerRegistration.Dispose();
        _totalTimeoutRegistration.Dispose();
        _attemptTimeoutRegistration.Dispose();
        Interlocked.Exchange(ref _transportCancellation, null)?.Dispose();
    }

    private void OnCancellation()
    {
        bool finalizeCancellation;
        lock (_gate)
        {
            if (_latched)
            {
                return;
            }

            _cancellationClaimed = true;
            _claimedCancellation = _claimedCancellation.Merge(CaptureCancellation());
            finalizeCancellation = !_cancellationFinalizationStarted;
            _cancellationFinalizationStarted = true;
        }

        _beforeCancellationObservation();
        if (!finalizeCancellation)
        {
            return;
        }

        AttemptArbitration decision;
        CancellationTokenSource? transportCancellation = null;
        lock (_gate)
        {
            if (_latched)
            {
                return;
            }

            _claimedCancellation = _claimedCancellation.Merge(CaptureCancellation());
            decision = LatchCancellation(_claimedCancellation);
            if (_transportStarted)
            {
                transportCancellation = Interlocked.Exchange(
                    ref _transportCancellation,
                    null);
            }
        }

        _completion.TrySetResult(decision);
        if (transportCancellation is not null)
        {
            QueueTransportCancellation(transportCancellation);
        }
    }

    private void OnTransportCompleted(Task<DistributionHttpResponse> completedTransport)
    {
        AttemptArbitration? decision = null;
        lock (_gate)
        {
            if (_latched)
            {
                ObserveFault(completedTransport);
                return;
            }

            if (_cancellationClaimed)
            {
                ObserveFault(completedTransport);
                return;
            }

            decision = LatchTransport(completedTransport);
        }

        _completion.TrySetResult(decision.Value);
    }

    private void ObserveCompletion(Task<DistributionHttpResponse> transportTask) =>
        _observeCompletion(transportTask, OnTransportCompleted);

    private static void ObserveCompletionWithTaskScheduler(
        Task<DistributionHttpResponse> transportTask,
        Action<Task<DistributionHttpResponse>> observer) =>
        _ = transportTask.ContinueWith(
            static (completed, state) =>
                ((Action<Task<DistributionHttpResponse>>)state!)(completed),
            observer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void QueueTransportStart(TransportStartWork startWork)
    {
        try
        {
            bool queued = ThreadPool.UnsafeQueueUserWorkItem(
                static work => work.Run(),
                startWork,
                preferLocal: false);
            if (!queued)
            {
                startWork.FailToQueue();
            }
        }
        catch (Exception exception)
        {
            startWork.FailToQueue(exception);
        }
    }

    private OwnedCancellationSnapshot CaptureCancellation() =>
        OwnedCancellationSnapshot.Capture(
            _callerToken,
            _totalTimeoutToken,
            _attemptTimeoutToken);

    private AttemptArbitration LatchTransport(Task<DistributionHttpResponse> completedTransport)
    {
        _latched = true;
        return new AttemptArbitration(completedTransport, true, default);
    }

    private AttemptArbitration LatchCancellation(OwnedCancellationSnapshot snapshot)
    {
        _latched = true;
        return new AttemptArbitration(null, _transportStarted, snapshot);
    }

    private static void QueueTransportCancellation(
        CancellationTokenSource transportCancellation)
    {
        try
        {
            bool queued = ThreadPool.UnsafeQueueUserWorkItem(
                static cancellation => CancelAndDisposeTransport(cancellation),
                transportCancellation,
                preferLocal: false);
            if (!queued)
            {
                transportCancellation.Dispose();
            }
        }
        catch (Exception)
        {
            transportCancellation.Dispose();
        }
    }

    private static void CancelAndDisposeTransport(
        CancellationTokenSource transportCancellation)
    {
        try
        {
            transportCancellation.Cancel();
        }
        catch (Exception)
        {
            // Downstream cancellation callbacks cannot change the latched package outcome.
        }
        finally
        {
            transportCancellation.Dispose();
        }
    }

    private static void ObserveFault(Task<DistributionHttpResponse> completedTransport)
    {
        if (completedTransport.IsFaulted)
        {
            _ = completedTransport.Exception;
        }
    }

    private sealed class TransportStartWork(
        Func<CancellationToken, Task<DistributionHttpResponse>> startTransport,
        CancellationToken cancellationToken,
        TaskCompletionSource<DistributionHttpResponse> proxy)
    {
        internal Task<DistributionHttpResponse> ProxyTask => proxy.Task;

        internal void Run()
        {
            Task<DistributionHttpResponse> transportTask;
            try
            {
                transportTask = startTransport(cancellationToken)
                    ?? Task.FromException<DistributionHttpResponse>(
                        new InvalidOperationException("The transport returned no task."));
            }
            catch (Exception exception)
            {
                proxy.TrySetException(exception);
                return;
            }

            _ = transportTask.ContinueWith(
                static (completed, state) =>
                    TransferCompletion(
                        completed,
                        (TaskCompletionSource<DistributionHttpResponse>)state!),
                proxy,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void FailToQueue(Exception? exception = null) =>
            proxy.TrySetException(
                exception ?? new InvalidOperationException("The transport could not be scheduled."));

        private static void TransferCompletion(
            Task<DistributionHttpResponse> completed,
            TaskCompletionSource<DistributionHttpResponse> destination)
        {
            if (completed.IsCanceled)
            {
                destination.TrySetCanceled();
            }
            else if (completed.IsFaulted)
            {
                destination.TrySetException(completed.Exception.InnerExceptions);
            }
            else
            {
                destination.TrySetResult(completed.Result);
            }
        }
    }
}
