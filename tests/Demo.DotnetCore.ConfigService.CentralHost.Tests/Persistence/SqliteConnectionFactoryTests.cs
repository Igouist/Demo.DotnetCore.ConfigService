using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteConnectionFactoryTests
{
    [Fact]
    public async Task OpenConnectionAsync_failed_configuration_preserves_the_configuration_failure_when_cleanup_fails()
    {
        var options = new CentralConfigDatabaseOptions(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = ":memory:",
                Pooling = false,
                ForeignKeys = false
            }.ConnectionString,
            ":memory:");
        var clearPoolCalls = 0;
        var cleanup = new SqliteConnectionCleanup(
            async connection =>
            {
                await connection.DisposeAsync();
                throw new InvalidOperationException("disposal-sentinel");
            },
            connection =>
            {
                clearPoolCalls++;
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            });
        var factory = new SqliteConnectionFactory(options, cleanup);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.OpenConnectionAsync(CancellationToken.None));

        Assert.Equal("SQLite foreign-key enforcement is not enabled.", failure.Message);
        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task OpenConnectionAsync_enforces_foreign_keys_on_each_connection()
    {
        await using var directory = TemporaryDirectory.Create();
        var factory = CreateFactory(directory.Path);

        await using var first = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var second = await factory.OpenConnectionAsync(CancellationToken.None);

        Assert.Equal(1, await first.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
        Assert.Equal(1, await second.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));

        await AssertForeignKeyEnforcementAsync(first, "First");
        await AssertForeignKeyEnforcementAsync(second, "Second");
    }

    [Fact]
    public async Task OpenConnectionAsync_registers_ordinal_ignore_case_collation_matching_domain_identity()
    {
        await using var directory = TemporaryDirectory.Create();
        var factory = CreateFactory(directory.Path);
        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);

        var pairs = new[]
        {
            (Left: "Demo:Message", Right: "demo:message"),
            (Left: "k", Right: "\u212A"),
            (Left: "straße", Right: "STRASSE")
        };

        foreach (var pair in pairs)
        {
            var sqliteEqual = await connection.ExecuteScalarAsync<long>(
                "SELECT CASE WHEN @Left = @Right COLLATE ORDINAL_IGNORE_CASE THEN 1 ELSE 0 END;",
                new { pair.Left, pair.Right });

            Assert.Equal(
                StringComparer.OrdinalIgnoreCase.Equals(pair.Left, pair.Right),
                sqliteEqual == 1);
        }
    }

    private static SqliteConnectionFactory CreateFactory(string contentRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CentralConfigDatabase"] = "Data Source=central-config.db;Pooling=False"
            })
            .Build();
        var options = CentralConfigDatabaseOptions.FromConfiguration(configuration, contentRoot);

        return new SqliteConnectionFactory(options);
    }

    private static async Task AssertForeignKeyEnforcementAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string tableSuffix)
    {
        await connection.ExecuteAsync($"""
            CREATE TABLE Parent{tableSuffix} (Id INTEGER PRIMARY KEY);
            CREATE TABLE Child{tableSuffix} (
                Id INTEGER PRIMARY KEY,
                ParentId INTEGER NOT NULL REFERENCES Parent{tableSuffix}(Id));
            """);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            connection.ExecuteAsync($"INSERT INTO Child{tableSuffix} (Id, ParentId) VALUES (1, 42);"));
    }
}
