namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

internal sealed class ControlledAttemptCompletionObservation
{
    private Task<DistributionHttpResponse>? _transportTask;
    private Action<Task<DistributionHttpResponse>>? _observer;

    internal void Capture(
        Task<DistributionHttpResponse> transportTask,
        Action<Task<DistributionHttpResponse>> observer)
    {
        if (_transportTask is not null || _observer is not null)
        {
            throw new InvalidOperationException("A completion observation is already captured.");
        }

        _transportTask = transportTask;
        _observer = observer;
    }

    internal void ObserveNow()
    {
        Task<DistributionHttpResponse> transportTask = _transportTask
            ?? throw new InvalidOperationException("No transport completion was captured.");
        Action<Task<DistributionHttpResponse>> observer = _observer
            ?? throw new InvalidOperationException("No transport observer was captured.");
        observer(transportTask);
    }

    internal Task WaitForTransportCompletionAsync() => (_transportTask
        ?? throw new InvalidOperationException("No transport completion was captured."))
        .WaitAsync(TimeSpan.FromSeconds(5));
}

internal sealed class BlockingCallbackGate : IDisposable
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource<bool> _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(false);

    internal void Block()
    {
        _entered.TrySetResult(true);
        _release.Wait();
    }

    internal Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(SafetyTimeout);

    internal void Release() => _release.Set();

    public void Dispose() => _release.Dispose();
}

internal sealed class CountingBlockingCallbackGate(int expectedCount) : IDisposable
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource<bool> _allEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(false);
    private int _enteredCount;

    internal void Block()
    {
        if (Interlocked.Increment(ref _enteredCount) == expectedCount)
        {
            _allEntered.TrySetResult(true);
        }

        _release.Wait();
    }

    internal Task WaitUntilAllEnteredAsync() => _allEntered.Task.WaitAsync(SafetyTimeout);

    internal void Release() => _release.Set();

    public void Dispose() => _release.Dispose();
}

internal sealed class ControlledSynchronizationContext : SynchronizationContext, IDisposable
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];
    private readonly SemaphoreSlim _posted = new(0);

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_callbacks)
        {
            _callbacks.Enqueue((d, state));
        }

        _posted.Release();
    }

    internal async Task<Action> TakeNextAsync()
    {
        if (!await _posted.WaitAsync(SafetyTimeout))
        {
            throw new TimeoutException("No continuation was posted before the safety timeout.");
        }
        (SendOrPostCallback Callback, object? State) work;
        lock (_callbacks)
        {
            work = _callbacks.Dequeue();
        }

        return () =>
        {
            SynchronizationContext? prior = Current;
            try
            {
                SetSynchronizationContext(this);
                work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(prior);
            }
        };
    }

    public void Dispose() => _posted.Dispose();
}

internal sealed class BlockingDistributionResponseParser : IDistributionResponseParser, IDisposable
{
    private readonly BlockingCallbackGate _gate = new();
    private readonly Exception? _failure;
    private readonly TaskCompletionSource<bool> _finished = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal BlockingDistributionResponseParser(Exception? failure = null)
    {
        _failure = failure;
    }

    internal Task WaitUntilEnteredAsync() => _gate.WaitUntilEnteredAsync();

    internal Task WaitUntilFinishedAsync() =>
        _finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

    internal void Release() => _gate.Release();

    public ValidatedDistributionResponse? ParseAndValidate(
        ReadOnlyMemory<byte> utf8Json,
        KeySelection requestedSelection)
    {
        try
        {
            _gate.Block();
            if (_failure is not null)
            {
                throw _failure;
            }

            return new ValidatedDistributionResponse(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Demo:Required"] = "central"
                },
                ["Demo:Fallback"]);
        }
        finally
        {
            _finished.TrySetResult(true);
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class BlockingSnapshotSourcePreparer : ISnapshotSourcePreparer, IDisposable
{
    private readonly BlockingCallbackGate _gate = new();
    private readonly Exception? _failure;
    private readonly TaskCompletionSource<bool> _finished = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal BlockingSnapshotSourcePreparer(Exception? failure = null)
    {
        _failure = failure;
    }

    internal Task WaitUntilEnteredAsync() => _gate.WaitUntilEnteredAsync();

    internal Task WaitUntilFinishedAsync() =>
        _finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

    internal void Release() => _gate.Release();

    public CentralConfigSnapshotSource Prepare(
        IReadOnlyDictionary<string, string> values)
    {
        try
        {
            _gate.Block();
            if (_failure is not null)
            {
                throw _failure;
            }

            return SnapshotSourcePreparer.Instance.Prepare(values);
        }
        finally
        {
            _finished.TrySetResult(true);
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class BlockingSnapshotCommitter : ISnapshotCommitter, IDisposable
{
    private readonly BlockingCallbackGate _gate = new();

    internal Task WaitUntilEnteredAsync() => _gate.WaitUntilEnteredAsync();

    internal void Release() => _gate.Release();

    public SnapshotCommitDecision TryCommit(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        Action commit)
    {
        _gate.Block();
        return SnapshotCommitter.Instance.TryCommit(
            callerToken,
            totalTimeoutToken,
            commit);
    }

    public void Dispose() => _gate.Dispose();
}
