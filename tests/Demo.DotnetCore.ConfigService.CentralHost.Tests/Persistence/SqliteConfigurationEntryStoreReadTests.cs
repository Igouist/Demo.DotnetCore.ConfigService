using System.Globalization;
using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteConfigurationEntryStoreReadTests
{
    private static readonly DateTimeOffset FirstTimestamp =
        new(2026, 8, 31, 8, 9, 10, TimeSpan.Zero);

    private static readonly ConfigurationEntryIdentity FirstIdentity = new(
        EnvironmentName.FromValidated("Production"),
        ConfigurationKey.FromValidated("Demo:Message"));

    [Fact]
    public async Task GetCurrentAsync_returns_null_only_for_an_identity_never_created()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var neverCreated = await fixture.Store.GetCurrentAsync(
            FirstIdentity,
            CancellationToken.None);

        Assert.Null(neverCreated);

        await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);

        var current = await fixture.Store.GetCurrentAsync(
            FirstIdentity,
            CancellationToken.None);

        var present = Assert.IsType<ConfigurationCurrent>(current);
        Assert.Equal("Production", present.Environment.Value);
        Assert.Equal("Demo:Message", present.Key.Value);
        Assert.Equal(1, present.Version);
        Assert.Equal(ConfigurationEntryState.Present, present.State);
        Assert.Equal("first-value", present.Value);
        Assert.Equal(FirstTimestamp, present.CreatedAtUtc);
    }

    [Fact]
    public async Task GetCurrentAsync_returns_removed_with_a_null_value_after_remove()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "before-remove",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.RemoveAsync(
            FirstIdentity,
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        var current = await fixture.Store.GetCurrentAsync(
            FirstIdentity,
            CancellationToken.None);

        var removed = Assert.IsType<ConfigurationCurrent>(current);
        Assert.Equal(2, removed.Version);
        Assert.Equal(ConfigurationEntryState.Removed, removed.State);
        Assert.Null(removed.Value);
        Assert.Equal(FirstTimestamp.AddMinutes(1), removed.CreatedAtUtc);
    }

    [Fact]
    public async Task GetCurrentAsync_preserves_an_empty_set_value_as_present()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            string.Empty,
            FirstTimestamp,
            CancellationToken.None);

        var current = await fixture.Store.GetCurrentAsync(
            FirstIdentity,
            CancellationToken.None);

        var present = Assert.IsType<ConfigurationCurrent>(current);
        Assert.Equal(ConfigurationEntryState.Present, present.State);
        Assert.Equal(string.Empty, present.Value);
    }

    [Fact]
    public async Task Current_and_history_use_ordinal_ignore_case_identity_and_preserve_first_set_spelling()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var storedIdentity = new ConfigurationEntryIdentity(
            EnvironmentName.FromValidated("PrØduction"),
            ConfigurationKey.FromValidated("Démø:Äpp"));
        await fixture.Store.SetAsync(
            storedIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(
                EnvironmentName.FromValidated("prødUCTION"),
                ConfigurationKey.FromValidated("dÉMØ:äpp")),
            "second-value",
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        var queryIdentity = new ConfigurationEntryIdentity(
            EnvironmentName.FromValidated("PRØDUCTION"),
            ConfigurationKey.FromValidated("DÉMØ:ÄPP"));
        var current = await fixture.Store.GetCurrentAsync(queryIdentity, CancellationToken.None);
        var history = await fixture.Store.GetHistoryAsync(queryIdentity, CancellationToken.None);

        var present = Assert.IsType<ConfigurationCurrent>(current);
        Assert.Equal("PrØduction", present.Environment.Value);
        Assert.Equal("Démø:Äpp", present.Key.Value);
        Assert.Equal("second-value", present.Value);
        var completeHistory = Assert.IsType<ConfigurationHistory>(history);
        Assert.Equal("PrØduction", completeHistory.Environment.Value);
        Assert.Equal("Démø:Äpp", completeHistory.Key.Value);
        Assert.Equal([2L, 1L], completeHistory.Versions.Select(static version => version.Version));
    }

    [Fact]
    public async Task GetHistoryAsync_returns_every_version_in_strictly_descending_order()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.SetAsync(
            FirstIdentity,
            "second-value",
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);
        await fixture.Store.RemoveAsync(
            FirstIdentity,
            FirstTimestamp.AddMinutes(2),
            CancellationToken.None);

        var history = await fixture.Store.GetHistoryAsync(
            FirstIdentity,
            CancellationToken.None);

        var completeHistory = Assert.IsType<ConfigurationHistory>(history);
        Assert.Equal([3L, 2L, 1L], completeHistory.Versions.Select(static version => version.Version));
        Assert.Collection(
            completeHistory.Versions,
            latest =>
            {
                Assert.Equal(ConfigurationOperation.Remove, latest.Operation);
                Assert.Null(latest.Value);
                Assert.Equal(FirstTimestamp.AddMinutes(2), latest.CreatedAtUtc);
            },
            middle =>
            {
                Assert.Equal(ConfigurationOperation.Set, middle.Operation);
                Assert.Equal("second-value", middle.Value);
                Assert.Equal(FirstTimestamp.AddMinutes(1), middle.CreatedAtUtc);
            },
            earliest =>
            {
                Assert.Equal(ConfigurationOperation.Set, earliest.Operation);
                Assert.Equal("first-value", earliest.Value);
                Assert.Equal(FirstTimestamp, earliest.CreatedAtUtc);
            });
    }

    [Fact]
    public async Task ResolveExistingAsync_returns_only_requested_existing_entries_in_request_order()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var secondIdentity = new ConfigurationEntryIdentity(
            EnvironmentName.FromValidated("Production"),
            ConfigurationKey.FromValidated("Demo:Second"));
        await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.SetAsync(
            secondIdentity,
            "second-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.RemoveAsync(
            secondIdentity,
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        var resolutions = await fixture.Store.ResolveExistingAsync(
            EnvironmentName.FromValidated("production"),
            [
                ConfigurationKey.FromValidated("demo:message"),
                ConfigurationKey.FromValidated("Demo:Missing"),
                ConfigurationKey.FromValidated("DEMO:SECOND")
            ],
            CancellationToken.None);

        Assert.Collection(
            resolutions,
            first =>
            {
                Assert.Equal("Demo:Message", first.Key.Value);
                Assert.Equal(ConfigurationEntryState.Present, first.State);
                Assert.Equal("first-value", first.Value);
            },
            second =>
            {
                Assert.Equal("Demo:Second", second.Key.Value);
                Assert.Equal(ConfigurationEntryState.Removed, second.State);
                Assert.Null(second.Value);
            });
    }

    [Fact]
    public async Task Reads_observe_the_previous_wal_commit_until_a_second_connection_commits_the_next_version()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "committed-value",
            FirstTimestamp,
            CancellationToken.None);
        await using var writer = await fixture.Connections.OpenConnectionAsync(CancellationToken.None);
        using var writerTransaction = writer.BeginTransaction(deferred: false);
        await writer.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO ConfigurationEntryVersions
                    (ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
                SELECT Id, 2, 'Set', @Value, @CreatedAtUtc, 'test-author'
                FROM ConfigurationEntries
                WHERE Environment = @Environment AND ConfigurationKey = @Key;
                """,
                new
                {
                    Environment = FirstIdentity.Environment.Value,
                    Key = FirstIdentity.Key.Value,
                    Value = "uncommitted-value",
                    CreatedAtUtc = FirstTimestamp.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture)
                },
                writerTransaction,
                cancellationToken: CancellationToken.None));

        var beforeCurrent = await fixture.Store.GetCurrentAsync(FirstIdentity, CancellationToken.None);
        var beforeHistory = await fixture.Store.GetHistoryAsync(FirstIdentity, CancellationToken.None);
        var beforeResolution = await fixture.Store.ResolveExistingAsync(
            FirstIdentity.Environment,
            [FirstIdentity.Key],
            CancellationToken.None);

        Assert.Equal("committed-value", Assert.IsType<ConfigurationCurrent>(beforeCurrent).Value);
        Assert.Equal([1L], Assert.IsType<ConfigurationHistory>(beforeHistory).Versions.Select(static version => version.Version));
        Assert.Equal("committed-value", Assert.Single(beforeResolution).Value);

        await writerTransaction.CommitAsync(CancellationToken.None);

        var afterCurrent = await fixture.Store.GetCurrentAsync(FirstIdentity, CancellationToken.None);
        var afterHistory = await fixture.Store.GetHistoryAsync(FirstIdentity, CancellationToken.None);
        var afterResolution = await fixture.Store.ResolveExistingAsync(
            FirstIdentity.Environment,
            [FirstIdentity.Key],
            CancellationToken.None);

        Assert.Equal("uncommitted-value", Assert.IsType<ConfigurationCurrent>(afterCurrent).Value);
        Assert.Equal([2L, 1L], Assert.IsType<ConfigurationHistory>(afterHistory).Versions.Select(static version => version.Version));
        Assert.Equal("uncommitted-value", Assert.Single(afterResolution).Value);
    }

    [Fact]
    public async Task ResolveExistingAsync_executes_1100_distinct_request_keys_without_a_parameter_ceiling()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var keys = Enumerable.Range(0, 1_100)
            .Select(static index => ConfigurationKey.FromValidated($"Bulk:Key{index:D4}"))
            .ToArray();
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(
                EnvironmentName.FromValidated("Production"),
                keys[0]),
            "bulk-value",
            FirstTimestamp,
            CancellationToken.None);

        var resolutions = await fixture.Store.ResolveExistingAsync(
            EnvironmentName.FromValidated("Production"),
            keys,
            CancellationToken.None);

        var resolution = Assert.Single(resolutions);
        Assert.Equal("Bulk:Key0000", resolution.Key.Value);
        Assert.Equal(ConfigurationEntryState.Present, resolution.State);
        Assert.Equal("bulk-value", resolution.Value);
    }

    [Fact]
    public async Task ResolveExistingAsync_with_connection_pooling_enabled_can_be_called_sequentially()
    {
        await using var fixture = await StoreFixture.CreateAsync(pooling: true);
        await fixture.Store.SetAsync(
            FirstIdentity,
            "pooled-value",
            FirstTimestamp,
            CancellationToken.None);

        var first = await fixture.Store.ResolveExistingAsync(
            FirstIdentity.Environment,
            [FirstIdentity.Key],
            CancellationToken.None);
        var second = await fixture.Store.ResolveExistingAsync(
            FirstIdentity.Environment,
            [FirstIdentity.Key],
            CancellationToken.None);

        Assert.Equal("pooled-value", Assert.Single(first).Value);
        Assert.Equal("pooled-value", Assert.Single(second).Value);
    }

    [Fact]
    public async Task Read_queries_translate_unknown_operations_and_state_value_mismatches_to_safe_other_failures()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.InsertUncheckedVersionAsync(
            FirstIdentity,
            2,
            "Unknown",
            null,
            FirstTimestamp.AddMinutes(1));

        var unknownOperation = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            fixture.Store.GetCurrentAsync(FirstIdentity, CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, unknownOperation.Category);
        Assert.Equal("The configuration store operation failed.", unknownOperation.Message);

        await fixture.DeleteVersionsAsync();
        await fixture.InsertUncheckedVersionAsync(
            FirstIdentity,
            1,
            "Set",
            null,
            FirstTimestamp);

        var mismatch = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            fixture.Store.GetHistoryAsync(FirstIdentity, CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, mismatch.Category);
        Assert.Equal("The configuration store operation failed.", mismatch.Message);
    }

    [Fact]
    public async Task ResolveExistingAsync_rejects_a_requested_entry_without_versions_as_a_safe_other_failure()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.DeleteVersionsAsync();

        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            fixture.Store.ResolveExistingAsync(
                FirstIdentity.Environment,
                [FirstIdentity.Key],
                CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, failure.Category);
        Assert.Equal("The configuration store operation failed.", failure.Message);
    }

    [Fact]
    public async Task ListEntriesAsync_returns_the_latest_version_of_every_entry_in_one_environment_ordered_by_key()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var production = EnvironmentName.FromValidated("Production");
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(production, ConfigurationKey.FromValidated("Demo:Zeta")),
            "zeta",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(production, ConfigurationKey.FromValidated("demo:alpha")),
            "alpha-1",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(production, ConfigurationKey.FromValidated("DEMO:ALPHA")),
            "alpha-2",
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(production, ConfigurationKey.FromValidated("Demo:Beta")),
            "beta",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.RemoveAsync(
            new ConfigurationEntryIdentity(production, ConfigurationKey.FromValidated("Demo:Beta")),
            FirstTimestamp.AddMinutes(2),
            CancellationToken.None);
        await fixture.Store.SetAsync(
            new ConfigurationEntryIdentity(
                EnvironmentName.FromValidated("UAT"),
                ConfigurationKey.FromValidated("Demo:OtherEnvironment")),
            "other",
            FirstTimestamp,
            CancellationToken.None);

        var entries = await fixture.Store.ListEntriesAsync(
            EnvironmentName.FromValidated("pRoDuCtIoN"),
            CancellationToken.None);

        Assert.Collection(
            entries,
            entry => AssertSummary(entry, "demo:alpha", 2, ConfigurationEntryState.Present, FirstTimestamp.AddMinutes(1)),
            entry => AssertSummary(entry, "Demo:Beta", 2, ConfigurationEntryState.Removed, FirstTimestamp.AddMinutes(2)),
            entry => AssertSummary(entry, "Demo:Zeta", 1, ConfigurationEntryState.Present, FirstTimestamp));
        Assert.Empty(await fixture.Store.ListEntriesAsync(
            EnvironmentName.FromValidated("NeverCreated"),
            CancellationToken.None));
    }

    [Fact]
    public async Task ListEntriesAsync_rejects_an_entry_without_versions_as_a_safe_other_failure()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.DeleteVersionsAsync();

        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            fixture.Store.ListEntriesAsync(FirstIdentity.Environment, CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, failure.Category);
        Assert.Equal("The configuration store operation failed.", failure.Message);
    }

    private static void AssertSummary(
        ConfigurationEntrySummary actual,
        string key,
        long version,
        ConfigurationEntryState state,
        DateTimeOffset createdAtUtc)
    {
        Assert.Equal(key, actual.Key.Value);
        Assert.Equal(version, actual.Version);
        Assert.Equal(state, actual.State);
        Assert.Equal(createdAtUtc, actual.CreatedAtUtc);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        private readonly bool _poolingEnabled;

        private StoreFixture(
            TemporaryDirectory directory,
            SqliteConnectionFactory connections,
            bool poolingEnabled)
        {
            _directory = directory;
            _poolingEnabled = poolingEnabled;
            Connections = connections;
            Store = new SqliteConfigurationEntryStore(connections);
        }

        public SqliteConnectionFactory Connections { get; }

        public IConfigurationEntryStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(bool pooling = false)
        {
            var directory = TemporaryDirectory.Create();
            try
            {
                var databasePath = Path.Combine(directory.Path, "central-config.db");
                var options = new CentralConfigDatabaseOptions(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath,
                        Pooling = pooling,
                        ForeignKeys = true,
                        DefaultTimeout = 5
                    }.ConnectionString,
                    databasePath);
                var connections = new SqliteConnectionFactory(options);
                await new SqliteDatabaseInitializer(options, connections)
                    .InitializeAsync(CancellationToken.None);
                return new StoreFixture(directory, connections, pooling);
            }
            catch
            {
                await directory.DisposeAsync();
                throw;
            }
        }

        public async Task InsertUncheckedVersionAsync(
            ConfigurationEntryIdentity identity,
            long version,
            string operation,
            string? value,
            DateTimeOffset createdAtUtc)
        {
            await using var connection = await Connections.OpenConnectionAsync(CancellationToken.None);
            await connection.ExecuteAsync("PRAGMA ignore_check_constraints = ON;");
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO ConfigurationEntryVersions
                        (ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
                    SELECT Id, @Version, @Operation, @Value, @CreatedAtUtc, 'test-author'
                    FROM ConfigurationEntries
                    WHERE Environment = @Environment AND ConfigurationKey = @Key;
                    """,
                    new
                    {
                        Environment = identity.Environment.Value,
                        Key = identity.Key.Value,
                        Version = version,
                        Operation = operation,
                        Value = value,
                        CreatedAtUtc = createdAtUtc.ToString("O", CultureInfo.InvariantCulture)
                    },
                    cancellationToken: CancellationToken.None));
        }

        public async Task DeleteVersionsAsync()
        {
            await using var connection = await Connections.OpenConnectionAsync(CancellationToken.None);
            await connection.ExecuteAsync("DELETE FROM ConfigurationEntryVersions;");
        }

        public async ValueTask DisposeAsync()
        {
            if (_poolingEnabled)
            {
                await using var connection = await Connections.OpenConnectionAsync(CancellationToken.None);
                SqliteConnection.ClearPool(connection);
            }

            await _directory.DisposeAsync();
        }
    }
}
