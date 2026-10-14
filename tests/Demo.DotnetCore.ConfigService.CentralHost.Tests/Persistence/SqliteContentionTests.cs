using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteContentionTests
{
    private static readonly DateTimeOffset FirstTimestamp =
        new(2026, 8, 31, 5, 6, 7, TimeSpan.Zero);

    private static readonly ConfigurationEntryIdentity FirstIdentity = new(
        EnvironmentName.FromValidated("Production"),
        ConfigurationKey.FromValidated("Demo:Message"));

    [Fact]
    public async Task Concurrent_sets_commit_exactly_versions_1_and_2()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var (first, second) = await RunTogetherAsync(
            () => fixture.Store.SetAsync(
                FirstIdentity,
                "first-concurrent-value",
                FirstTimestamp,
                CancellationToken.None),
            () => fixture.Store.SetAsync(
                FirstIdentity,
                "second-concurrent-value",
                FirstTimestamp.AddMinutes(1),
                CancellationToken.None));

        Assert.Equal([1L, 2L], new[] { first.Version, second.Version }.Order());
        var versions = await fixture.ReadVersionsAsync();
        Assert.Equal([1L, 2L], versions.Select(static row => row.Version));
        Assert.Equal(
            ["first-concurrent-value", "second-concurrent-value"],
            versions.Select(static row => row.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Concurrent_removes_create_one_version_and_return_one_already_removed()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "initial-value",
            FirstTimestamp,
            CancellationToken.None);

        var (first, second) = await RunTogetherAsync(
            () => fixture.Store.RemoveAsync(
                FirstIdentity,
                FirstTimestamp.AddMinutes(1),
                CancellationToken.None),
            () => fixture.Store.RemoveAsync(
                FirstIdentity,
                FirstTimestamp.AddMinutes(2),
                CancellationToken.None));

        Assert.Equal(1, new RemoveStoreResult[] { first, second }.Count(static result => result is RemoveStoreResult.Created));
        Assert.Equal(1, new RemoveStoreResult[] { first, second }.Count(static result => result is RemoveStoreResult.AlreadyRemoved));
        var versions = await fixture.ReadVersionsAsync();
        Assert.Equal(["Set", "Remove"], versions.Select(static row => row.Operation));
    }

    [Fact]
    public async Task Concurrent_set_and_remove_commit_consecutive_versions_with_last_version_as_final_state()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "initial-value",
            FirstTimestamp,
            CancellationToken.None);

        var (set, removeResult) = await RunTogetherAsync(
            () => fixture.Store.SetAsync(
                FirstIdentity,
                "concurrent-set-value",
                FirstTimestamp.AddMinutes(1),
                CancellationToken.None),
            () => fixture.Store.RemoveAsync(
                FirstIdentity,
                FirstTimestamp.AddMinutes(2),
                CancellationToken.None));

        var remove = Assert.IsType<RemoveStoreResult.Created>(removeResult).Receipt;
        Assert.Equal([2L, 3L], new[] { set.Version, remove.Version }.Order());
        var versions = await fixture.ReadVersionsAsync();
        Assert.Equal([1L, 2L, 3L], versions.Select(static row => row.Version));
        var last = versions[2];
        if (set.Version > remove.Version)
        {
            Assert.Equal("Set", last.Operation);
            Assert.Equal("concurrent-set-value", last.Value);
        }
        else
        {
            Assert.Equal("Remove", last.Operation);
            Assert.Null(last.Value);
        }
    }

    [Fact]
    public async Task Execution_boundary_invokes_failing_persistence_operation_once()
    {
        var attempts = 0;

        var failure = await Assert.ThrowsAsync<PersistenceBusyException>(() =>
            SqliteFailureTranslator.ExecuteAsync(() =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException<int>(new SqliteException("test-only busy", 5));
            }));

        Assert.Equal(1, attempts);
        Assert.IsType<SqliteException>(failure.InnerException);
    }

    [Fact]
    public async Task Immediate_transaction_contention_is_terminal_busy_without_application_retry()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = HoldImmediateTransactionAsync(
            fixture.Connections,
            lockAcquired,
            releaseLock);
        await lockAcquired.Task;
        var invocationCount = 0;

        try
        {
            async Task InvokeStoreOnceAsync()
            {
                Interlocked.Increment(ref invocationCount);
                await fixture.Store.SetAsync(
                    FirstIdentity,
                    "busy-sensitive-value",
                    FirstTimestamp,
                    CancellationToken.None);
            }

            var failure = await Assert.ThrowsAsync<PersistenceBusyException>(InvokeStoreOnceAsync);

            Assert.Equal(1, invocationCount);
            Assert.Equal("The configuration store is busy.", failure.Message);
            Assert.IsType<SqliteException>(failure.InnerException);
            Assert.DoesNotContain("busy-sensitive-value", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.DatabasePath, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            releaseLock.TrySetResult();
            await blocker;
        }

        Assert.Equal(0, await fixture.CountEntriesAsync());
        Assert.Empty(await fixture.ReadVersionsAsync());
    }

    [Fact]
    public async Task Shared_cache_reader_contention_is_terminal_locked()
    {
        await using var fixture = await StoreFixture.CreateAsync(SqliteCacheMode.Shared);
        await fixture.Store.SetAsync(
            FirstIdentity,
            "reader-lock-seed",
            FirstTimestamp,
            CancellationToken.None);
        var readerAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = HoldVersionReaderAsync(
            fixture.Connections,
            readerAcquired,
            releaseReader);
        await readerAcquired.Task;
        var secondIdentity = new ConfigurationEntryIdentity(
            EnvironmentName.FromValidated("Production"),
            ConfigurationKey.FromValidated("Demo:Second"));

        try
        {
            var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
                fixture.Store.SetAsync(
                    secondIdentity,
                    "locked-sensitive-value",
                    FirstTimestamp.AddMinutes(1),
                    CancellationToken.None));

            Assert.Equal(PersistenceFailureCategory.Locked, failure.Category);
            var sqliteFailure = Assert.IsType<SqliteException>(failure.InnerException);
            Assert.Equal(6, sqliteFailure.SqliteErrorCode);
            Assert.Equal("The configuration store operation failed.", failure.Message);
            Assert.DoesNotContain("locked-sensitive-value", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.DatabasePath, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            releaseReader.TrySetResult();
            await reader;
        }

        Assert.Equal(1, await fixture.CountEntriesAsync());
        Assert.Single(await fixture.ReadVersionsAsync());
    }

    private static async Task<(TFirst First, TSecond Second)> RunTogetherAsync<TFirst, TSecond>(
        Func<Task<TFirst>> first,
        Func<Task<TSecond>> second)
    {
        using var barrier = new Barrier(2);
        var firstTask = StartBehindBarrierAsync(barrier, first);
        var secondTask = StartBehindBarrierAsync(barrier, second);
        await Task.WhenAll(firstTask, secondTask);
        return (await firstTask, await secondTask);
    }

    private static Task<T> StartBehindBarrierAsync<T>(Barrier barrier, Func<Task<T>> operation) =>
        Task.Run(async () =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The concurrent store operations did not reach the test barrier.");
            }

            return await operation();
        });

    private static async Task HoldImmediateTransactionAsync(
        SqliteConnectionFactory connections,
        TaskCompletionSource lockAcquired,
        TaskCompletionSource releaseLock)
    {
        await using var connection = await connections.OpenConnectionAsync(CancellationToken.None);
        using var transaction = connection.BeginTransaction(deferred: false);
        lockAcquired.SetResult();
        await releaseLock.Task;
        transaction.Commit();
    }

    private static async Task HoldVersionReaderAsync(
        SqliteConnectionFactory connections,
        TaskCompletionSource readerAcquired,
        TaskCompletionSource releaseReader)
    {
        await using var connection = await connections.OpenConnectionAsync(CancellationToken.None);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM ConfigurationEntryVersions ORDER BY Version;";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The reader-lock seed version was not found.");
        }

        readerAcquired.SetResult();
        await releaseReader.Task;
        transaction.Commit();
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;

        private StoreFixture(
            TemporaryDirectory directory,
            string databasePath,
            SqliteConnectionFactory connections)
        {
            _directory = directory;
            DatabasePath = databasePath;
            Connections = connections;
            Store = new SqliteConfigurationEntryStore(connections);
        }

        public string DatabasePath { get; }

        public SqliteConnectionFactory Connections { get; }

        public IConfigurationEntryStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(
            SqliteCacheMode cache = SqliteCacheMode.Default)
        {
            var directory = TemporaryDirectory.Create();
            try
            {
                var databasePath = Path.Combine(directory.Path, "central-config.db");
                var options = new CentralConfigDatabaseOptions(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath,
                        Pooling = false,
                        ForeignKeys = true,
                        DefaultTimeout = 5,
                        Cache = cache
                    }.ConnectionString,
                    databasePath);
                var connections = new SqliteConnectionFactory(options);
                await new SqliteDatabaseInitializer(options, connections)
                    .InitializeAsync(CancellationToken.None);
                return new StoreFixture(directory, databasePath, connections);
            }
            catch
            {
                await directory.DisposeAsync();
                throw;
            }
        }

        public async Task<long> CountEntriesAsync()
        {
            await using var connection = await Connections.OpenConnectionAsync(CancellationToken.None);
            return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ConfigurationEntries;");
        }

        public async Task<IReadOnlyList<VersionRow>> ReadVersionsAsync()
        {
            await using var connection = await Connections.OpenConnectionAsync(CancellationToken.None);
            var rows = await connection.QueryAsync<VersionRow>("""
                SELECT Version, Operation, Value, CreatedAtUtc
                FROM ConfigurationEntryVersions
                ORDER BY Version;
                """);
            return rows.AsList();
        }

        public ValueTask DisposeAsync() => _directory.DisposeAsync();
    }

    private sealed class VersionRow
    {
        public long Version { get; init; }

        public string Operation { get; init; } = string.Empty;

        public string? Value { get; init; }

        public string CreatedAtUtc { get; init; } = string.Empty;
    }
}
