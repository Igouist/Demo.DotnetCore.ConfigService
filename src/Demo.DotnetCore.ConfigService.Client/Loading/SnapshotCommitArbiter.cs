namespace Demo.DotnetCore.ConfigService.Client;

internal interface ISnapshotCommitter
{
    SnapshotCommitDecision TryCommit(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        Action commit);
}

internal sealed class SnapshotCommitter : ISnapshotCommitter
{
    internal static SnapshotCommitter Instance { get; } = new();

    private SnapshotCommitter()
    {
    }

    public SnapshotCommitDecision TryCommit(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken,
        Action commit)
    {
        using var arbiter = new SnapshotCommitArbiter(
            callerToken,
            totalTimeoutToken);
        return arbiter.TryCommit(commit);
    }
}

internal readonly record struct SnapshotCommitDecision(
    bool IsCommitted,
    OwnedCancellationSnapshot Cancellation);

internal sealed class SnapshotCommitArbiter : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _totalTimeoutToken;
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly CancellationTokenRegistration _totalTimeoutRegistration;

    private SnapshotCommitDecision _decision;
    private bool _latched;

    internal SnapshotCommitArbiter(
        CancellationToken callerToken,
        CancellationToken totalTimeoutToken)
    {
        _callerToken = callerToken;
        _totalTimeoutToken = totalTimeoutToken;
        _callerRegistration = callerToken.UnsafeRegister(
            static state => ((SnapshotCommitArbiter)state!).OnCancellation(),
            this);
        _totalTimeoutRegistration = totalTimeoutToken.UnsafeRegister(
            static state => ((SnapshotCommitArbiter)state!).OnCancellation(),
            this);
    }

    internal SnapshotCommitDecision TryCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        SnapshotCommitDecision decision;
        lock (_gate)
        {
            if (!_latched)
            {
                OwnedCancellationSnapshot cancellation = CaptureCancellation();
                _decision = cancellation.IsCancellationRequested
                    ? new SnapshotCommitDecision(false, cancellation)
                    : new SnapshotCommitDecision(true, default);
                _latched = true;
            }

            decision = _decision;
        }

        if (decision.IsCommitted)
        {
            // Configuration-source insertion is the irreversible commit point; once it wins, later cancellation cannot roll back the inserted source.
            commit();
        }

        return decision;
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

            _decision = new SnapshotCommitDecision(false, CaptureCancellation());
            _latched = true;
        }
    }

    private OwnedCancellationSnapshot CaptureCancellation() =>
        OwnedCancellationSnapshot.Capture(
            _callerToken,
            _totalTimeoutToken,
            CancellationToken.None);
}
