using System.Globalization;
using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteConfigurationEntryStoreWriteTests
{
    private static readonly DateTimeOffset FirstTimestamp =
        new(2026, 8, 31, 1, 2, 3, TimeSpan.Zero);

    private static readonly ConfigurationEntryIdentity FirstIdentity = new(
        EnvironmentName.FromValidated("Production"),
        ConfigurationKey.FromValidated("Demo:Message"));

    [Fact]
    public async Task SetAsync_creates_version_1_as_present_with_first_input_display_spelling()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var receipt = await fixture.Store.SetAsync(
            FirstIdentity,
            "first-value",
            FirstTimestamp,
            CancellationToken.None);

        Assert.Equal("Production", receipt.Environment.Value);
        Assert.Equal("Demo:Message", receipt.Key.Value);
        Assert.Equal(1, receipt.Version);
        Assert.Equal(ConfigurationEntryState.Present, receipt.State);
        Assert.Equal(FirstTimestamp, receipt.CreatedAtUtc);

        var versions = await fixture.ReadVersionsAsync();
        var version = Assert.Single(versions);
        Assert.Equal(1, version.Version);
        Assert.Equal("Set", version.Operation);
        Assert.Equal("first-value", version.Value);
        Assert.Equal(FirstTimestamp.ToString("O", CultureInfo.InvariantCulture), version.CreatedAtUtc);
    }

    [Fact]
    public async Task SetAsync_with_equal_value_appends_version_2()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "same-value",
            FirstTimestamp,
            CancellationToken.None);

        var receipt = await fixture.Store.SetAsync(
            FirstIdentity,
            "same-value",
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(2, receipt.Version);
        var versions = await fixture.ReadVersionsAsync();
        Assert.Collection(
            versions,
            first =>
            {
                Assert.Equal(1, first.Version);
                Assert.Equal("same-value", first.Value);
            },
            second =>
            {
                Assert.Equal(2, second.Version);
                Assert.Equal("same-value", second.Value);
            });
    }

    [Fact]
    public async Task SetAsync_after_remove_appends_present_restore()
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

        var receipt = await fixture.Store.SetAsync(
            FirstIdentity,
            "restored",
            FirstTimestamp.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(3, receipt.Version);
        Assert.Equal(ConfigurationEntryState.Present, receipt.State);
        var versions = await fixture.ReadVersionsAsync();
        Assert.Equal(["Set", "Remove", "Set"], versions.Select(static row => row.Operation));
        Assert.Equal("restored", versions[2].Value);
    }

    [Fact]
    public async Task RemoveAsync_against_never_created_returns_not_found_without_inserting_rows()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var result = await fixture.Store.RemoveAsync(
            FirstIdentity,
            FirstTimestamp,
            CancellationToken.None);

        Assert.IsType<RemoveStoreResult.NotFound>(result);
        Assert.Equal(0, await fixture.CountEntriesAsync());
        Assert.Empty(await fixture.ReadVersionsAsync());
    }

    [Fact]
    public async Task RemoveAsync_against_present_appends_remove_with_null_value()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "value-to-remove",
            FirstTimestamp,
            CancellationToken.None);

        var result = await fixture.Store.RemoveAsync(
            FirstIdentity,
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        var created = Assert.IsType<RemoveStoreResult.Created>(result);
        Assert.Equal(2, created.Receipt.Version);
        Assert.Equal(ConfigurationEntryState.Removed, created.Receipt.State);
        var versions = await fixture.ReadVersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal("Remove", versions[1].Operation);
        Assert.Null(versions[1].Value);
    }

    [Fact]
    public async Task RemoveAsync_against_removed_returns_already_removed_without_inserting_a_row()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "value-to-remove",
            FirstTimestamp,
            CancellationToken.None);
        await fixture.Store.RemoveAsync(
            FirstIdentity,
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        var result = await fixture.Store.RemoveAsync(
            FirstIdentity,
            FirstTimestamp.AddMinutes(2),
            CancellationToken.None);

        Assert.IsType<RemoveStoreResult.AlreadyRemoved>(result);
        Assert.Equal(2, (await fixture.ReadVersionsAsync()).Count);
    }

    [Fact]
    public async Task Later_commands_with_different_casing_return_first_set_display_spelling()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SetAsync(
            FirstIdentity,
            "first",
            FirstTimestamp,
            CancellationToken.None);
        var differentlyCased = new ConfigurationEntryIdentity(
            EnvironmentName.FromValidated("production"),
            ConfigurationKey.FromValidated("demo:message"));

        var setReceipt = await fixture.Store.SetAsync(
            differentlyCased,
            "second",
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);
        var remove = await fixture.Store.RemoveAsync(
            new ConfigurationEntryIdentity(
                EnvironmentName.FromValidated("PRODUCTION"),
                ConfigurationKey.FromValidated("DEMO:MESSAGE")),
            FirstTimestamp.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal("Production", setReceipt.Environment.Value);
        Assert.Equal("Demo:Message", setReceipt.Key.Value);
        var removeReceipt = Assert.IsType<RemoveStoreResult.Created>(remove).Receipt;
        Assert.Equal("Production", removeReceipt.Environment.Value);
        Assert.Equal("Demo:Message", removeReceipt.Key.Value);
        Assert.Equal(1, await fixture.CountEntriesAsync());
    }

    [Fact]
    public async Task Failed_version_insert_rolls_back_new_entry_and_next_set_starts_at_version_1()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TRIGGER FailVersionInsert
            BEFORE INSERT ON ConfigurationEntryVersions
            BEGIN
                SELECT RAISE(ABORT, 'test-only');
            END;
            """);

        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            fixture.Store.SetAsync(
                FirstIdentity,
                "sensitive-value",
                FirstTimestamp,
                CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, failure.Category);
        Assert.Equal("The configuration store operation failed.", failure.Message);
        Assert.DoesNotContain("test-only", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.DatabasePath, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountEntriesAsync());
        Assert.Empty(await fixture.ReadVersionsAsync());

        await fixture.ExecuteAsync("DROP TRIGGER FailVersionInsert;");
        var receipt = await fixture.Store.SetAsync(
            FirstIdentity,
            "successful-value",
            FirstTimestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, receipt.Version);
        Assert.Equal(1, await fixture.CountEntriesAsync());
        Assert.Single(await fixture.ReadVersionsAsync());
    }

    [Fact]
    public async Task SetAsync_at_Int64_max_version_returns_other_failure_without_wrapping()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.ExecuteAsync(
            """
            INSERT INTO ConfigurationEntries (Id, Environment, ConfigurationKey)
            VALUES (1, 'Production', 'Demo:Message');

            INSERT INTO ConfigurationEntryVersions
                (ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES
                (1, @Version, 'Set', 'last-value', @CreatedAtUtc, 'test-author');
            """,
            new
            {
                Version = long.MaxValue,
                CreatedAtUtc = FirstTimestamp.ToString("O", CultureInfo.InvariantCulture)
            });

        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() =>
            fixture.Store.SetAsync(
                FirstIdentity,
                "must-not-be-inserted",
                FirstTimestamp.AddMinutes(1),
                CancellationToken.None));

        Assert.Equal(PersistenceFailureCategory.Other, failure.Category);
        Assert.IsType<OverflowException>(failure.InnerException);
        Assert.Equal("The configuration store operation failed.", failure.Message);
        var version = Assert.Single(await fixture.ReadVersionsAsync());
        Assert.Equal(long.MaxValue, version.Version);
        Assert.Equal("last-value", version.Value);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        private readonly SqliteConnectionFactory _connections;

        private StoreFixture(
            TemporaryDirectory directory,
            string databasePath,
            SqliteConnectionFactory connections)
        {
            _directory = directory;
            DatabasePath = databasePath;
            _connections = connections;
            Store = new SqliteConfigurationEntryStore(connections);
        }

        public string DatabasePath { get; }

        public IConfigurationEntryStore Store { get; }

        public static async Task<StoreFixture> CreateAsync()
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
                        DefaultTimeout = 5
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

        public async Task ExecuteAsync(string sql, object? parameters = null)
        {
            await using var connection = await _connections.OpenConnectionAsync(CancellationToken.None);
            await connection.ExecuteAsync(sql, parameters);
        }

        public async Task<long> CountEntriesAsync()
        {
            await using var connection = await _connections.OpenConnectionAsync(CancellationToken.None);
            return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ConfigurationEntries;");
        }

        public async Task<IReadOnlyList<VersionRow>> ReadVersionsAsync()
        {
            await using var connection = await _connections.OpenConnectionAsync(CancellationToken.None);
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
