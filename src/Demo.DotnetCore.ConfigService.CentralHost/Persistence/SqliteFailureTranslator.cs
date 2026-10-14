using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class SqliteFailureTranslator
{
    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> persistenceOperation)
    {
        try
        {
            return await persistenceOperation();
        }
        catch (Exception exception)
        {
            var translated = Translate(exception);
            if (ReferenceEquals(translated, exception))
            {
                throw;
            }

            throw translated;
        }
    }

    public static Exception Translate(Exception exception) => exception switch
    {
        OperationCanceledException => exception,
        PersistenceBusyException or PersistenceFailureException => exception,
        SqliteException { SqliteErrorCode: 5 } => new PersistenceBusyException(exception),
        SqliteException { SqliteErrorCode: 6 } =>
            new PersistenceFailureException(PersistenceFailureCategory.Locked, exception),
        SqliteException or OverflowException =>
            new PersistenceFailureException(PersistenceFailureCategory.Other, exception),
        _ => exception
    };
}
