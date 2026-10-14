using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteConnectionCleanupTests
{
    [Fact]
    public async Task DisposeAsync_successful_disposal_does_not_clear_the_pool()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var disposeCalls = 0;
        var clearPoolCalls = 0;
        var cleanup = new SqliteConnectionCleanup(
            _ =>
            {
                disposeCalls++;
                return Task.CompletedTask;
            },
            _ => clearPoolCalls++);

        await cleanup.DisposeAsync(connection, pendingFailure: null);

        Assert.Equal(1, disposeCalls);
        Assert.Equal(0, clearPoolCalls);
    }

    [Fact]
    public async Task DisposeAsync_failed_disposal_without_a_pending_failure_clears_the_pool_and_rethrows_the_same_failure()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var disposalFailure = new InvalidOperationException("disposal-sentinel");
        var clearPoolCalls = 0;
        var cleanup = new SqliteConnectionCleanup(
            _ => Task.FromException(disposalFailure),
            _ => clearPoolCalls++);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cleanup.DisposeAsync(connection, pendingFailure: null));

        Assert.Same(disposalFailure, thrown);
        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task DisposeAsync_failed_disposal_with_a_pending_failure_clears_the_pool_and_returns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var pendingFailure = new InvalidOperationException("operation-sentinel");
        var clearPoolCalls = 0;
        var cleanup = new SqliteConnectionCleanup(
            _ => Task.FromException(new InvalidOperationException("disposal-sentinel")),
            _ => clearPoolCalls++);

        await cleanup.DisposeAsync(connection, pendingFailure);

        Assert.Equal(1, clearPoolCalls);
        Assert.Equal("operation-sentinel", pendingFailure.Message);
    }

    [Fact]
    public async Task DisposeAsync_pool_clear_failure_does_not_replace_the_disposal_failure()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var disposalFailure = new InvalidOperationException("disposal-sentinel");
        var cleanup = new SqliteConnectionCleanup(
            _ => Task.FromException(disposalFailure),
            _ => throw new InvalidOperationException("clear-pool-sentinel"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cleanup.DisposeAsync(connection, pendingFailure: null));

        Assert.Same(disposalFailure, thrown);
    }

    [Fact]
    public async Task DisposeAsync_pool_clear_failure_does_not_replace_a_pending_failure()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var pendingFailure = new InvalidOperationException("operation-sentinel");
        var cleanup = new SqliteConnectionCleanup(
            _ => Task.FromException(new InvalidOperationException("disposal-sentinel")),
            _ => throw new InvalidOperationException("clear-pool-sentinel"));

        await cleanup.DisposeAsync(connection, pendingFailure);

        Assert.Equal("operation-sentinel", pendingFailure.Message);
    }

    [Fact]
    public async Task DisposeAsync_preserves_the_exact_pending_cancellation_for_its_caller()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancellation = new OperationCanceledException(cancellationSource.Token);
        var clearPoolCalls = 0;
        var cleanup = new SqliteConnectionCleanup(
            _ => Task.FromException(new InvalidOperationException("disposal-sentinel")),
            _ => clearPoolCalls++);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ThrowAfterCleanupAsync(cleanup, connection, cancellation));

        Assert.Same(cancellation, thrown);
        Assert.Equal(cancellationSource.Token, thrown.CancellationToken);
        Assert.Equal(1, clearPoolCalls);
    }

    private static async Task ThrowAfterCleanupAsync(
        SqliteConnectionCleanup cleanup,
        SqliteConnection connection,
        Exception pendingFailure)
    {
        try
        {
            throw pendingFailure;
        }
        catch (Exception exception)
        {
            try
            {
                throw;
            }
            finally
            {
                await cleanup.DisposeAsync(connection, exception);
            }
        }
    }
}
