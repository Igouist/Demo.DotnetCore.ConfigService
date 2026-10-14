namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class RetiringResourceOwnerTests
{
    [Fact]
    public async Task Retirement_atomically_moves_active_ownership_and_blocks_replacement()
    {
        var owner = new RetiringResourceOwner<TestResource>();
        var active = new TestResource(HasExited: false);
        var replacement = new TestResource(HasExited: false);
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource<ResourceCleanupOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Attach(active);

        Task retirement = owner.RetireAsync(
            (_, _) =>
            {
                cleanupEntered.TrySetResult();
                return releaseCleanup.Task;
            });
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(owner.Active);
        Assert.Same(active, owner.Retiring);
        Assert.Same(active, owner.Owned);
        Action attachReplacement = () => owner.Attach(replacement);
        Assert.Throws<InvalidOperationException>(attachReplacement);

        releaseCleanup.TrySetResult(new ResourceCleanupOutcome(
            CleanupCompleted: true,
            Failure: null));
        await retirement.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(owner.Active);
        Assert.Null(owner.Retiring);
        Assert.Null(owner.Owned);
        owner.Attach(replacement);
        Assert.Same(replacement, owner.Active);
    }

    [Fact]
    public async Task Cleanup_failure_remains_owned_and_final_retirement_retries_it()
    {
        var owner = new RetiringResourceOwner<TestResource>();
        var failedResource = new TestResource(HasExited: false);
        var replacement = new TestResource(HasExited: false);
        var cleanupFailure = new InvalidOperationException("bounded cleanup failed");
        int cleanupAttempts = 0;
        owner.Attach(failedResource);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.RetireAsync(
                (_, _) =>
                {
                    cleanupAttempts++;
                    return Task.FromResult(new ResourceCleanupOutcome(
                        CleanupCompleted: false,
                        Failure: cleanupFailure));
                }));

        Assert.Same(cleanupFailure, thrown);
        Assert.Equal(1, cleanupAttempts);
        Assert.Null(owner.Active);
        Assert.Same(failedResource, owner.Retiring);
        Assert.Same(failedResource, owner.Owned);
        Action attachReplacement = () => owner.Attach(replacement);
        Assert.Throws<InvalidOperationException>(attachReplacement);

        await owner.RetireAsync(
            (resource, _) =>
            {
                cleanupAttempts++;
                Assert.Same(failedResource, resource);
                return Task.FromResult(new ResourceCleanupOutcome(
                    CleanupCompleted: true,
                    Failure: null));
            });

        Assert.Equal(2, cleanupAttempts);
        Assert.Null(owner.Retiring);
        Assert.Null(owner.Owned);
        owner.Attach(replacement);
        Assert.Same(replacement, owner.Active);
    }

    [Fact]
    public async Task Exited_resource_cleanup_failure_before_relaunch_retains_ownership()
    {
        var owner = new RetiringResourceOwner<TestResource>();
        var exitedResource = new TestResource(HasExited: true);
        var replacement = new TestResource(HasExited: false);
        var cleanupFailure = new TimeoutException("bounded disposal failed");
        owner.Attach(exitedResource);

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            owner.PrepareForLaunchAsync(
                static resource => resource.HasExited,
                (_, _) => Task.FromResult(new ResourceCleanupOutcome(
                    CleanupCompleted: false,
                    Failure: cleanupFailure))));

        Assert.Same(cleanupFailure, thrown);
        Assert.Null(owner.Active);
        Assert.Same(exitedResource, owner.Retiring);
        Assert.Same(exitedResource, owner.Owned);
        Action attachReplacement = () => owner.Attach(replacement);
        Assert.Throws<InvalidOperationException>(attachReplacement);
    }

    [Fact]
    public async Task Completed_cleanup_clears_ownership_before_propagating_cancellation()
    {
        var owner = new RetiringResourceOwner<TestResource>();
        var resource = new TestResource(HasExited: false);
        var cancellation = new OperationCanceledException(new CancellationToken(canceled: true));
        owner.Attach(resource);

        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => owner.RetireAsync(
                (_, _) => Task.FromResult(new ResourceCleanupOutcome(
                    CleanupCompleted: true,
                    Failure: cancellation))));

        Assert.Same(cancellation, thrown);
        Assert.Null(owner.Active);
        Assert.Null(owner.Retiring);
        Assert.Null(owner.Owned);
    }

    [Fact]
    public async Task Non_cancellation_failure_retains_ownership_even_if_disposal_completed()
    {
        var owner = new RetiringResourceOwner<TestResource>();
        var resource = new TestResource(HasExited: false);
        var cleanupFailure = new InvalidOperationException("bounded cleanup failed");
        owner.Attach(resource);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.RetireAsync(
                (_, _) => Task.FromResult(new ResourceCleanupOutcome(
                    CleanupCompleted: true,
                    Failure: cleanupFailure))));

        Assert.Same(cleanupFailure, thrown);
        Assert.Null(owner.Active);
        Assert.Same(resource, owner.Retiring);
        Assert.Same(resource, owner.Owned);
    }

    private sealed record TestResource(bool HasExited);
}
