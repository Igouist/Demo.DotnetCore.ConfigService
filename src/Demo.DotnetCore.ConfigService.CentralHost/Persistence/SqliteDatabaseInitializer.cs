using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class SqliteDatabaseInitializer
{
    private const string ConfigurationEntriesTable = "ConfigurationEntries";
    private const string ConfigurationEntryVersionsTable = "ConfigurationEntryVersions";
    private const string LatestIndex = "IX_ConfigurationEntryVersions_Latest";
    private const string IncompatibleMessage = "The Central Config database schema is incompatible.";
    private const string InitializationFailedMessage = "The Central Config database could not be initialized.";

    private static readonly ExpectedColumn[] ConfigurationEntriesColumns =
    [
        new("Id", "INTEGER", IsNotNull: false, PrimaryKeyOrder: 1),
        new("Environment", "TEXT", IsNotNull: true, PrimaryKeyOrder: 0),
        new("ConfigurationKey", "TEXT", IsNotNull: true, PrimaryKeyOrder: 0)
    ];

    private static readonly ExpectedColumn[] ConfigurationEntryVersionsColumns =
    [
        new("Id", "INTEGER", IsNotNull: false, PrimaryKeyOrder: 1),
        new("ConfigurationEntryId", "INTEGER", IsNotNull: true, PrimaryKeyOrder: 0),
        new("Version", "INTEGER", IsNotNull: true, PrimaryKeyOrder: 0),
        new("Operation", "TEXT", IsNotNull: true, PrimaryKeyOrder: 0),
        new("Value", "TEXT", IsNotNull: false, PrimaryKeyOrder: 0),
        new("CreatedAtUtc", "TEXT", IsNotNull: true, PrimaryKeyOrder: 0)
    ];

    private readonly CentralConfigDatabaseOptions _options;
    private readonly SqliteConnectionFactory _connections;

    public SqliteDatabaseInitializer(
        CentralConfigDatabaseOptions options,
        SqliteConnectionFactory connections)
    {
        _options = options;
        _connections = connections;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var parentDirectory = Path.GetDirectoryName(_options.DataSourcePath)
                ?? throw new InvalidOperationException();
            Directory.CreateDirectory(parentDirectory);

            await ExecuteWithConnectionAsync(async connection =>
            {
                var journalMode = await connection.ExecuteScalarAsync<string>(
                    new CommandDefinition(
                        "PRAGMA journal_mode=WAL;",
                        cancellationToken: cancellationToken));
                if (!StringComparer.OrdinalIgnoreCase.Equals(journalMode, "wal"))
                {
                    throw new DatabaseInitializationException();
                }

                var version = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        "PRAGMA user_version;",
                        cancellationToken: cancellationToken));

                if (version == 0)
                {
                    await CreateVersion1Async(connection, cancellationToken);
                    return;
                }

                if (version != 1 || !await IsVersion1CompatibleAsync(connection, cancellationToken))
                {
                    throw new IncompatibleDatabaseException();
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IncompatibleDatabaseException)
        {
            throw new InvalidOperationException(IncompatibleMessage);
        }
        catch
        {
            throw new InvalidOperationException(InitializationFailedMessage);
        }
    }

    public async Task VerifyReadinessAsync(CancellationToken cancellationToken)
    {
        await ExecuteWithConnectionAsync(async connection =>
        {
            long version = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "PRAGMA user_version;",
                    cancellationToken: cancellationToken));

            if (version != 1 || !await IsVersion1CompatibleAsync(connection, cancellationToken))
            {
                throw new IncompatibleDatabaseException();
            }
        }, cancellationToken);
    }

    private async Task ExecuteWithConnectionAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken)
    {
        SqliteConnection? connection = null;
        Exception? operationFailure = null;
        try
        {
            connection = await _connections.OpenConnectionAsync(cancellationToken);
            await operation(connection);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (connection is not null)
            {
                await _connections.DisposeConnectionAsync(connection, operationFailure);
            }
        }
    }

    private static async Task CreateVersion1Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var pocTableCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name COLLATE NOCASE IN ('ConfigurationEntries', 'ConfigurationEntryVersions');
                """,
                cancellationToken: cancellationToken));
        if (pocTableCount != 0)
        {
            throw new IncompatibleDatabaseException();
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                SqliteSchema.CreateVersion1,
                transaction: transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsVersion1CompatibleAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await HasExpectedColumnsAsync(
                connection,
                ConfigurationEntriesTable,
                ConfigurationEntriesColumns,
                cancellationToken)
            || !await HasExpectedColumnsAsync(
                connection,
                ConfigurationEntryVersionsTable,
                ConfigurationEntryVersionsColumns,
                cancellationToken))
        {
            return false;
        }

        var tableSql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @TableName;",
                new { TableName = ConfigurationEntryVersionsTable },
                cancellationToken: cancellationToken));
        if (!HasRequiredChecks(tableSql))
        {
            return false;
        }

        var foreignKeys = (await connection.QueryAsync<ForeignKeyRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       seq AS Sequence,
                       "table" AS ReferencedTable,
                       "from" AS SourceColumn,
                       "to" AS TargetColumn
                FROM pragma_foreign_key_list(@TableName);
                """,
                new { TableName = ConfigurationEntryVersionsTable },
                cancellationToken: cancellationToken))).AsList();
        if (!foreignKeys
            .GroupBy(static foreignKey => foreignKey.Id)
            .Any(static foreignKey =>
                foreignKey.Count() == 1
                && foreignKey.Single() is var row
                && row.Sequence == 0
                && StringComparer.Ordinal.Equals(row.ReferencedTable, ConfigurationEntriesTable)
                && StringComparer.Ordinal.Equals(row.SourceColumn, "ConfigurationEntryId")
                && StringComparer.Ordinal.Equals(row.TargetColumn, "Id")))
        {
            return false;
        }

        return await HasIndexAsync(
                connection,
                ConfigurationEntriesTable,
                unique: true,
                requiredName: null,
                [
                    new("Environment", Descending: false, SqliteConnectionFactory.OrdinalIgnoreCaseCollation),
                    new("ConfigurationKey", Descending: false, SqliteConnectionFactory.OrdinalIgnoreCaseCollation)
                ],
                cancellationToken)
            && await HasIndexAsync(
                connection,
                ConfigurationEntryVersionsTable,
                unique: true,
                requiredName: null,
                [
                    new("ConfigurationEntryId", Descending: false, "BINARY"),
                    new("Version", Descending: false, "BINARY")
                ],
                cancellationToken)
            && await HasIndexAsync(
                connection,
                ConfigurationEntryVersionsTable,
                unique: false,
                LatestIndex,
                [
                    new("ConfigurationEntryId", Descending: false, "BINARY"),
                    new("Version", Descending: true, "BINARY")
                ],
                cancellationToken);
    }

    private static async Task<bool> HasExpectedColumnsAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<ExpectedColumn> expectedColumns,
        CancellationToken cancellationToken)
    {
        var actualColumns = (await connection.QueryAsync<TableColumnRow>(
            new CommandDefinition(
                """
                SELECT name AS Name,
                       type AS Type,
                       "notnull" AS IsNotNull,
                       pk AS PrimaryKeyOrder,
                       hidden AS Hidden
                FROM pragma_table_xinfo(@TableName)
                ORDER BY cid;
                """,
                new { TableName = tableName },
                cancellationToken: cancellationToken))).AsList();

        return actualColumns.Count == expectedColumns.Count
            && actualColumns.Zip(expectedColumns).All(static pair =>
                pair.First.Hidden == 0
                && StringComparer.Ordinal.Equals(pair.First.Name, pair.Second.Name)
                && StringComparer.OrdinalIgnoreCase.Equals(pair.First.Type, pair.Second.Type)
                && (pair.First.IsNotNull != 0) == pair.Second.IsNotNull
                && pair.First.PrimaryKeyOrder == pair.Second.PrimaryKeyOrder);
    }

    private static bool HasRequiredChecks(string? tableSql)
    {
        if (tableSql is null)
        {
            return false;
        }

        // Compare normalized CHECK semantics, not raw DDL: equivalent formatting is compatible, but partial schemas without required domain constraints are rejected.
        var checks = ExtractCheckExpressions(tableSql);
        return checks.Contains("VERSION>=1")
            && checks.Contains("OPERATIONIN('Set','Remove')")
            && checks.Contains(
                "(OPERATION='Set'ANDVALUEISNOTNULL)OR(OPERATION='Remove'ANDVALUEISNULL)");
    }

    private static HashSet<string> ExtractCheckExpressions(string tableSql)
    {
        var checks = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        while (position < tableSql.Length)
        {
            if (TrySkipTrivia(tableSql, ref position))
            {
                continue;
            }

            if (IsQuoteStart(tableSql[position]))
            {
                SkipQuoted(tableSql, ref position);
                continue;
            }

            if (!IsIdentifierCharacter(tableSql[position]))
            {
                position++;
                continue;
            }

            var identifierStart = position;
            while (position < tableSql.Length && IsIdentifierCharacter(tableSql[position]))
            {
                position++;
            }

            if (!tableSql.AsSpan(identifierStart, position - identifierStart)
                    .Equals("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            while (TrySkipTrivia(tableSql, ref position))
            {
            }

            if (position >= tableSql.Length || tableSql[position] != '(')
            {
                continue;
            }

            var expressionStart = ++position;
            var depth = 1;
            while (position < tableSql.Length && depth > 0)
            {
                if (TrySkipTrivia(tableSql, ref position))
                {
                    continue;
                }

                if (IsQuoteStart(tableSql[position]))
                {
                    SkipQuoted(tableSql, ref position);
                    continue;
                }

                if (tableSql[position] == '(')
                {
                    depth++;
                }
                else if (tableSql[position] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        checks.Add(NormalizeCheckExpression(
                            tableSql.AsSpan(expressionStart, position - expressionStart)));
                    }
                }

                position++;
            }
        }

        return checks;
    }

    private static string NormalizeCheckExpression(ReadOnlySpan<char> expression)
    {
        var normalized = new StringBuilder(expression.Length);
        var position = 0;
        while (position < expression.Length)
        {
            if (TrySkipTrivia(expression, ref position))
            {
                continue;
            }

            var character = expression[position];
            if (IsQuoteStart(character))
            {
                AppendQuoted(expression, ref position, normalized);
                continue;
            }

            normalized.Append(char.ToUpperInvariant(character));
            position++;
        }

        return normalized.ToString();
    }

    private static bool TrySkipTrivia(string sql, ref int position) =>
        TrySkipTrivia(sql.AsSpan(), ref position);

    private static bool TrySkipTrivia(ReadOnlySpan<char> sql, ref int position)
    {
        var start = position;
        while (position < sql.Length && char.IsWhiteSpace(sql[position]))
        {
            position++;
        }

        if (position + 1 < sql.Length && sql[position] == '-' && sql[position + 1] == '-')
        {
            position += 2;
            while (position < sql.Length && sql[position] is not '\r' and not '\n')
            {
                position++;
            }
        }
        else if (position + 1 < sql.Length && sql[position] == '/' && sql[position + 1] == '*')
        {
            position += 2;
            while (position + 1 < sql.Length && (sql[position] != '*' || sql[position + 1] != '/'))
            {
                position++;
            }

            position = Math.Min(position + 2, sql.Length);
        }

        return position != start;
    }

    private static void SkipQuoted(string sql, ref int position)
    {
        var ignored = new StringBuilder();
        AppendQuoted(sql.AsSpan(), ref position, ignored);
    }

    private static void AppendQuoted(
        ReadOnlySpan<char> sql,
        ref int position,
        StringBuilder destination)
    {
        var opening = sql[position];
        var closing = opening == '[' ? ']' : opening;
        destination.Append(opening);
        position++;

        while (position < sql.Length)
        {
            var character = sql[position++];
            destination.Append(character);
            if (character != closing)
            {
                continue;
            }

            if (position < sql.Length && sql[position] == closing)
            {
                destination.Append(sql[position++]);
                continue;
            }

            return;
        }
    }

    private static bool IsQuoteStart(char character) => character is '\'' or '"' or '`' or '[';

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '$';

    private static async Task<bool> HasIndexAsync(
        SqliteConnection connection,
        string tableName,
        bool unique,
        string? requiredName,
        IReadOnlyList<ExpectedIndexColumn> expectedColumns,
        CancellationToken cancellationToken)
    {
        var indexes = await connection.QueryAsync<IndexListRow>(
            new CommandDefinition(
                """
                SELECT name AS Name,
                       "unique" AS IsUnique,
                       partial AS IsPartial
                FROM pragma_index_list(@TableName);
                """,
                new { TableName = tableName },
                cancellationToken: cancellationToken));

        foreach (var index in indexes)
        {
            if ((index.IsUnique != 0) != unique
                || index.IsPartial != 0
                || (requiredName is not null
                    && !StringComparer.Ordinal.Equals(index.Name, requiredName)))
            {
                continue;
            }

            var columns = (await connection.QueryAsync<IndexColumnRow>(
                new CommandDefinition(
                    """
                    SELECT name AS Name,
                           "desc" AS IsDescending,
                           coll AS Collation
                    FROM pragma_index_xinfo(@IndexName)
                    WHERE key = 1
                    ORDER BY seqno;
                    """,
                    new { IndexName = index.Name },
                    cancellationToken: cancellationToken))).AsList();
            if (columns.Count == expectedColumns.Count
                && columns.Zip(expectedColumns).All(static pair =>
                    StringComparer.Ordinal.Equals(pair.First.Name, pair.Second.Name)
                    && (pair.First.IsDescending != 0) == pair.Second.Descending
                    && StringComparer.OrdinalIgnoreCase.Equals(pair.First.Collation, pair.Second.Collation)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool IsNotNull,
        long PrimaryKeyOrder);

    private sealed record ExpectedIndexColumn(
        string Name,
        bool Descending,
        string Collation);

    private sealed class TableColumnRow
    {
        public string Name { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public long IsNotNull { get; init; }

        public long PrimaryKeyOrder { get; init; }

        public long Hidden { get; init; }
    }

    private sealed class ForeignKeyRow
    {
        public long Id { get; init; }

        public long Sequence { get; init; }

        public string ReferencedTable { get; init; } = string.Empty;

        public string SourceColumn { get; init; } = string.Empty;

        public string TargetColumn { get; init; } = string.Empty;
    }

    private sealed class IndexListRow
    {
        public string Name { get; init; } = string.Empty;

        public long IsUnique { get; init; }

        public long IsPartial { get; init; }
    }

    private sealed class IndexColumnRow
    {
        public string Name { get; init; } = string.Empty;

        public long IsDescending { get; init; }

        public string Collation { get; init; } = string.Empty;
    }

    private sealed class IncompatibleDatabaseException : Exception;

    private sealed class DatabaseInitializationException : Exception;
}
