namespace Demo.DotnetCore.ConfigService.Client;

internal readonly record struct RetryScheduleDecision(
    bool IsScheduled,
    OwnedCancellationSnapshot Cancellation);

internal sealed class RetryScheduleArbiter : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _totalTimeoutToken;
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly CancellationTokenRegistration _totalTimeoutRegistration;

    private RetryScheduleDecision _decision;
    private bool _latched;

    internal RetryScheduleArbiter(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken)
    {
        _callerToken = callerToken;
        _totalTimeoutToken = totalTimeoutToken;
        _callerRegistration = callerToken.UnsafeRegister(
            static state => ((RetryScheduleArbiter)state!).OnCancellation(),
            this);
        _totalTimeoutRegistration = totalTimeoutToken.UnsafeRegister(
            static state => ((RetryScheduleArbiter)state!).OnCancellation(),
            this);
    }

    internal RetryScheduleDecision TrySchedule()
    {
        lock (_gate)
        {
            if (_latched)
            {
                return _decision;
            }

            OwnedCancellationSnapshot snapshot = CaptureCancellation();
            _decision = snapshot.IsCancellationRequested
                ? new RetryScheduleDecision(false, snapshot)
                : new RetryScheduleDecision(true, default);
            _latched = true;
            return _decision;
        }
    }

    public void Dispose()
    {
        _callerRegistration.Dispose();
        _totalTimeoutRegistration.Dispose();
    }

    private void OnCancellation()
    {
        lock (_gate)
        {
            if (_latched)
            {
                return;
            }

            _decision = new RetryScheduleDecision(false, CaptureCancellation());
            _latched = true;
        }
    }

    private OwnedCancellationSnapshot CaptureCancellation() =>
        OwnedCancellationSnapshot.Capture(
            _callerToken,
            _totalTimeoutToken,
            CancellationToken.None);
}
