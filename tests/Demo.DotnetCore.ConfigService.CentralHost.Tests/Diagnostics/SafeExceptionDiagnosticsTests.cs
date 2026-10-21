using Dapper;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Diagnostics;

public sealed class SafeExceptionDiagnosticsTests
{
    [Fact]
    public void Describe_lists_type_names_without_general_exception_messages()
    {
        var exception = new PersistenceFailureException(
            PersistenceFailureCategory.Other,
            new InvalidOperationException("configuration-value-sentinel"));

        Assert.Equal(
            "PersistenceFailureException -> InvalidOperationException",
            SafeExceptionDiagnostics.Describe(exception));
    }

    [Fact]
    public async Task Describe_keeps_the_sqlite_error_code_and_engine_message()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var sqliteException = await Assert.ThrowsAsync<SqliteException>(() =>
            connection.ExecuteAsync(
                "INSERT INTO MissingTable (Value) VALUES (@Value);",
                new { Value = "configuration-value-sentinel" }));

        string description = SafeExceptionDiagnostics.Describe(
            new PersistenceFailureException(PersistenceFailureCategory.Other, sqliteException));

        Assert.StartsWith("PersistenceFailureException -> SqliteException 1/1: ", description, StringComparison.Ordinal);
        Assert.Contains("no such table: MissingTable", description, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration-value-sentinel", description, StringComparison.Ordinal);
    }
}
