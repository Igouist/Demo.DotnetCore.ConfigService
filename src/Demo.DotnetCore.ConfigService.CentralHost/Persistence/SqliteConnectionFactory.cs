using Dapper;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class SqliteConnectionFactory
{
    public const string OrdinalIgnoreCaseCollation = "ORDINAL_IGNORE_CASE";

    private readonly CentralConfigDatabaseOptions _options;
    private readonly SqliteConnectionCleanup _cleanup;

    public SqliteConnectionFactory(
        CentralConfigDatabaseOptions options,
        SqliteConnectionCleanup? cleanup = null)
    {
        _options = options;
        _cleanup = cleanup ?? new SqliteConnectionCleanup();
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            connection.CreateCollation(
                OrdinalIgnoreCaseCollation,
                static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));

            var enabled = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "PRAGMA foreign_keys;",
                    cancellationToken: cancellationToken));
            if (enabled != 1)
            {
                throw new InvalidOperationException("SQLite foreign-key enforcement is not enabled.");
            }

            return connection;
        }
        catch (Exception exception)
        {
            await _cleanup.DisposeAsync(connection, exception);
            throw;
        }
    }

    internal Task DisposeConnectionAsync(SqliteConnection connection, Exception? pendingFailure) =>
        _cleanup.DisposeAsync(connection, pendingFailure);

    internal void ClearConnectionPool(SqliteConnection connection) =>
        _cleanup.ClearPoolBestEffort(connection);
}
