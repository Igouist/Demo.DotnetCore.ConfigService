using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteConnectionCleanupOwnerTests
{
    [Fact]
    public async Task Store_read_write_and_resolve_translate_a_primary_connection_disposal_failure()
    {
        await using var directory = TemporaryDirectory.Create();
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
        var initializerConnections = new SqliteConnectionFactory(options);
        await new SqliteDatabaseInitializer(options, initializerConnections)
            .InitializeAsync(CancellationToken.None);

        var clearPoolCalls = 0;
        var cleanup = new SqliteConnectionCleanup(
            async connection =>
            {
                await connection.DisposeAsync();
                throw new OverflowException("disposal-sentinel");
            },
            connection =>
            {
                clearPoolCalls++;
                SqliteConnection.ClearPool(connection);
            });
        var store = new SqliteConfigurationEntryStore(new SqliteConnectionFactory(options, cleanup));
        var identity = new ConfigurationEntryIdentity(
            EnvironmentName.FromValidated("Production"),
            ConfigurationKey.FromValidated("Demo:Message"));

        await AssertOtherPersistenceFailureAsync(() => store.SetAsync(
            identity,
            "value",
            new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None));
        await AssertOtherPersistenceFailureAsync(() => store.GetCurrentAsync(identity, CancellationToken.None));
        await AssertOtherPersistenceFailureAsync(() => store.ResolveExistingAsync(
            identity.Environment,
            [identity.Key],
            CancellationToken.None));

        Assert.Equal(3, clearPoolCalls);
    }

    private static async Task AssertOtherPersistenceFailureAsync(Func<Task> operation)
    {
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(operation);

        Assert.Equal(PersistenceFailureCategory.Other, failure.Category);
        Assert.Equal("The configuration store operation failed.", failure.Message);
    }
}
