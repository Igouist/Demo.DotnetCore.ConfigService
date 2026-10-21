using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost;

// POC-039: general exception messages may carry configuration values, so only type names are recorded.
// SQLite engine messages name schema objects and constraints, never bound values, so they keep the root cause.
internal static class SafeExceptionDiagnostics
{
    public static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current is SqliteException sqlite
                ? $"{nameof(SqliteException)} {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}: {sqlite.Message}"
                : current.GetType().Name);
        }

        return string.Join(" -> ", parts);
    }

    public static string? InnermostStackTrace(Exception exception) =>
        exception.GetBaseException().StackTrace;
}
