using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class SqliteConnectionCleanup
{
    private readonly Func<SqliteConnection, Task> _disposeAsync;
    private readonly Action<SqliteConnection> _clearPool;

    public SqliteConnectionCleanup()
        : this(
            static connection => connection.DisposeAsync().AsTask(),
            static connection => SqliteConnection.ClearPool(connection))
    {
    }

    internal SqliteConnectionCleanup(
        Func<SqliteConnection, Task> disposeAsync,
        Action<SqliteConnection> clearPool)
    {
        _disposeAsync = disposeAsync;
        _clearPool = clearPool;
    }

    internal async Task DisposeAsync(SqliteConnection connection, Exception? pendingFailure)
    {
        try
        {
            await _disposeAsync(connection);
        }
        catch
        {
            ClearPoolBestEffort(connection);

            if (pendingFailure is null)
            {
                throw;
            }
        }
    }

    internal void ClearPoolBestEffort(SqliteConnection connection)
    {
        try
        {
            _clearPool(connection);
        }
        catch
        {
            // Pool clearing is best effort and must not replace the disposal failure.
        }
    }
}
