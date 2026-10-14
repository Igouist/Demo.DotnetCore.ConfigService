using System.Runtime.ExceptionServices;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record ResourceCleanupOutcome(
    bool CleanupCompleted,
    Exception? Failure);

internal sealed class RetiringResourceOwner<T>
    where T : class
{
    private readonly object ownershipGate = new();
    private T? active;
    private T? retiring;

    internal T? Active
    {
        get
        {
            lock (ownershipGate)
            {
                return active;
            }
        }
    }

    internal T? Retiring
    {
        get
        {
            lock (ownershipGate)
            {
                return retiring;
            }
        }
    }

    internal T? Owned
    {
        get
        {
            lock (ownershipGate)
            {
                return active ?? retiring;
            }
        }
    }

    internal void Attach(T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        lock (ownershipGate)
        {
            if (active is not null || retiring is not null)
            {
                throw new InvalidOperationException(
                    "A resource cannot be attached while another resource is owned.");
            }

            active = resource;
        }
    }

    internal async Task PrepareForLaunchAsync(
        Func<T, bool> hasExited,
        Func<T, CancellationToken, Task<ResourceCleanupOutcome>> cleanup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(cleanup);
        cancellationToken.ThrowIfCancellationRequested();

        T? resource;
        lock (ownershipGate)
        {
            if (retiring is not null)
            {
                throw new InvalidOperationException(
                    "A replacement cannot start while resource cleanup is incomplete.");
            }

            resource = active;
            if (resource is null)
            {
                return;
            }

            if (!hasExited(resource))
            {
                throw new InvalidOperationException(
                    "A replacement cannot start while the active resource is live.");
            }

            active = null;
            retiring = resource;
        }

        await CleanupRetiringAsync(resource, cleanup, cancellationToken);
    }

    internal async Task RetireAsync(
        Func<T, CancellationToken, Task<ResourceCleanupOutcome>> cleanup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        cancellationToken.ThrowIfCancellationRequested();

        T? resource;
        lock (ownershipGate)
        {
            if (retiring is null)
            {
                retiring = active;
                active = null;
            }

            resource = retiring;
        }

        if (resource is not null)
        {
            await CleanupRetiringAsync(resource, cleanup, cancellationToken);
        }
    }

    private async Task CleanupRetiringAsync(
        T resource,
        Func<T, CancellationToken, Task<ResourceCleanupOutcome>> cleanup,
        CancellationToken cancellationToken)
    {
        ResourceCleanupOutcome outcome = await cleanup(resource, cancellationToken);

        if (outcome.CleanupCompleted
            && (outcome.Failure is null || outcome.Failure is OperationCanceledException))
        {
            lock (ownershipGate)
            {
                if (ReferenceEquals(retiring, resource))
                {
                    retiring = null;
                }
            }
        }

        if (outcome.Failure is not null)
        {
            ExceptionDispatchInfo.Capture(outcome.Failure).Throw();
        }

        if (!outcome.CleanupCompleted)
        {
            throw new InvalidOperationException("Resource cleanup did not complete.");
        }
    }
}
