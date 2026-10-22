using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class SqliteConfigurationEntryStore : IConfigurationEntryStore
{
    private const string SelectEntrySql = """
        SELECT Id, Environment, ConfigurationKey
        FROM ConfigurationEntries
        WHERE Environment = @Environment AND ConfigurationKey = @Key;
        """;

    private const string InsertEntrySql = """
        INSERT INTO ConfigurationEntries (Environment, ConfigurationKey)
        VALUES (@Environment, @Key)
        RETURNING Id;
        """;

    private const string SelectLatestVersionSql = """
        SELECT Version, Operation
        FROM ConfigurationEntryVersions
        WHERE ConfigurationEntryId = @EntryId
        ORDER BY Version DESC
        LIMIT 1;
        """;

    private const string InsertVersionSql = """
        INSERT INTO ConfigurationEntryVersions
            (ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc)
        VALUES
            (@EntryId, @Version, @Operation, @Value, @CreatedAtUtc);
        """;

    private const string SelectCurrentSql = """
        SELECT
            e.Environment,
            e.ConfigurationKey,
            v.Version,
            v.Operation,
            v.Value,
            v.CreatedAtUtc
        FROM ConfigurationEntries AS e
        JOIN ConfigurationEntryVersions AS v
          ON v.ConfigurationEntryId = e.Id
        WHERE e.Environment = @Environment
          AND e.ConfigurationKey = @Key
        ORDER BY v.Version DESC
        LIMIT 1;
        """;

    private const string SelectHistorySql = """
        SELECT
            e.Environment,
            e.ConfigurationKey,
            v.Version,
            v.Operation,
            v.Value,
            v.CreatedAtUtc
        FROM ConfigurationEntries AS e
        JOIN ConfigurationEntryVersions AS v
          ON v.ConfigurationEntryId = e.Id
        WHERE e.Environment = @Environment
          AND e.ConfigurationKey = @Key
        ORDER BY v.Version DESC;
        """;

    // The LEFT JOIN keeps an entry without versions visible so the read can reject that corrupt shape.
    private const string ListEntriesSql = """
        WITH LatestVersions AS (
            SELECT
                ConfigurationEntryId,
                Version,
                Operation,
                Value,
                CreatedAtUtc,
                ROW_NUMBER() OVER (
                    PARTITION BY ConfigurationEntryId
                    ORDER BY Version DESC
                ) AS RowNumber
            FROM ConfigurationEntryVersions
        )
        SELECT
            e.Environment,
            e.ConfigurationKey,
            latest.Version,
            latest.Operation,
            latest.Value,
            latest.CreatedAtUtc
        FROM ConfigurationEntries AS e
        LEFT JOIN LatestVersions AS latest
          ON latest.ConfigurationEntryId = e.Id
         AND latest.RowNumber = 1
        WHERE e.Environment = @Environment
        ORDER BY e.ConfigurationKey;
        """;

    private const string EntryExistsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM ConfigurationEntries
            WHERE Environment = @Environment AND ConfigurationKey = @Key
        );
        """;

    private const string CreateRequestedConfigurationKeysSql = """
        CREATE TEMP TABLE RequestedConfigurationKeys (
            Ordinal    INTEGER PRIMARY KEY,
            RequestKey TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE,
            UNIQUE (RequestKey)
        );
        """;

    private const string InsertRequestedConfigurationKeySql = """
        INSERT INTO RequestedConfigurationKeys (Ordinal, RequestKey)
        VALUES (@Ordinal, @RequestKey);
        """;

    private const string DropRequestedConfigurationKeysSql = """
        DROP TABLE IF EXISTS RequestedConfigurationKeys;
        """;

    private const string ResolveExistingSql = """
        WITH LatestVersions AS (
            SELECT
                ConfigurationEntryId,
                Version,
                Operation,
                Value,
                ROW_NUMBER() OVER (
                    PARTITION BY ConfigurationEntryId
                    ORDER BY Version DESC
                ) AS RowNumber
            FROM ConfigurationEntryVersions
        )
        SELECT
            requested.Ordinal,
            entry.ConfigurationKey,
            latest.Version,
            latest.Operation,
            latest.Value
        FROM RequestedConfigurationKeys AS requested
        JOIN ConfigurationEntries AS entry
          ON entry.Environment = @Environment
         AND entry.ConfigurationKey = requested.RequestKey
        JOIN LatestVersions AS latest
          ON latest.ConfigurationEntryId = entry.Id
         AND latest.RowNumber = 1
        ORDER BY requested.Ordinal;
        """;

    private const string RequestedEntryWithoutVersionsSql = """
        SELECT EXISTS (
            SELECT 1
            FROM RequestedConfigurationKeys AS requested
            JOIN ConfigurationEntries AS entry
              ON entry.Environment = @Environment
             AND entry.ConfigurationKey = requested.RequestKey
            WHERE NOT EXISTS (
                SELECT 1
                FROM ConfigurationEntryVersions AS version
                WHERE version.ConfigurationEntryId = entry.Id
            )
        );
        """;

    private readonly SqliteConnectionFactory _connections;

    public SqliteConfigurationEntryStore(SqliteConnectionFactory connections) => _connections = connections;

    public Task<ConfigurationCurrent?> GetCurrentAsync(
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken) =>
        ExecuteReadAsync(
            async (connection, transaction) =>
            {
                var row = await connection.QuerySingleOrDefaultAsync<StoredReadVersion>(
                    new CommandDefinition(
                        SelectCurrentSql,
                        new
                        {
                            Environment = identity.Environment.Value,
                            Key = identity.Key.Value
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                if (row is null)
                {
                    await ThrowIfEntryHasNoVersionsAsync(
                        connection,
                        transaction,
                        identity,
                        cancellationToken);
                    return null;
                }

                return ToCurrent(row);
            },
            cancellationToken);

    public Task<ConfigurationHistory?> GetHistoryAsync(
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken) =>
        ExecuteReadAsync(
            async (connection, transaction) =>
            {
                var rows = (await connection.QueryAsync<StoredReadVersion>(
                    new CommandDefinition(
                        SelectHistorySql,
                        new
                        {
                            Environment = identity.Environment.Value,
                            Key = identity.Key.Value
                        },
                        transaction,
                        cancellationToken: cancellationToken))).AsList();
                if (rows.Count == 0)
                {
                    await ThrowIfEntryHasNoVersionsAsync(
                        connection,
                        transaction,
                        identity,
                        cancellationToken);
                    return null;
                }

                var firstIdentity = ToIdentity(rows[0]);
                var versions = new List<ConfigurationVersion>(rows.Count);
                long? previousVersion = null;
                foreach (var row in rows)
                {
                    var storedIdentity = ToIdentity(row);
                    if (!storedIdentity.Equals(firstIdentity))
                    {
                        throw CorruptStoredShape("Stored history rows do not share one identity.");
                    }

                    var version = ToVersion(row);
                    if (previousVersion is not null && version.Version >= previousVersion.Value)
                    {
                        throw CorruptStoredShape("Stored history versions are not strictly descending.");
                    }

                    versions.Add(version);
                    previousVersion = version.Version;
                }

                return new ConfigurationHistory(
                    firstIdentity.Environment,
                    firstIdentity.Key,
                    versions);
            },
            cancellationToken);

    public Task<IReadOnlyList<ConfigurationEntrySummary>> ListEntriesAsync(
        EnvironmentName environment,
        CancellationToken cancellationToken) =>
        ExecuteReadAsync<IReadOnlyList<ConfigurationEntrySummary>>(
            async (connection, transaction) =>
            {
                var rows = await connection.QueryAsync<StoredEntrySummaryRow>(
                    new CommandDefinition(
                        ListEntriesSql,
                        new { Environment = environment.Value },
                        transaction,
                        cancellationToken: cancellationToken));
                return rows.Select(ToSummary).ToArray();
            },
            cancellationToken);

    public Task<IReadOnlyList<StoredResolution>> ResolveExistingAsync(
        EnvironmentName environment,
        IReadOnlyList<ConfigurationKey> keys,
        CancellationToken cancellationToken) =>
        ExecuteWithConnectionAsync(async connection =>
        {
            Exception? operationFailure = null;
            try
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        CreateRequestedConfigurationKeysSql,
                        cancellationToken: cancellationToken));
                for (var ordinal = 0; ordinal < keys.Count; ordinal++)
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            InsertRequestedConfigurationKeySql,
                            new
                            {
                                Ordinal = ordinal,
                                RequestKey = keys[ordinal].Value
                            },
                            cancellationToken: cancellationToken));
                }

                return await ExecuteDeferredReadAsync(
                    connection,
                    async transaction =>
                    {
                        await ThrowIfAnyRequestedEntryHasNoVersionsAsync(
                            connection,
                            transaction,
                            environment,
                            cancellationToken);
                        var rows = (await connection.QueryAsync<StoredResolutionRow>(
                            new CommandDefinition(
                                ResolveExistingSql,
                                new { Environment = environment.Value },
                                transaction,
                                cancellationToken: cancellationToken))).AsList();
                        IReadOnlyList<StoredResolution> resolutions = rows
                            .Select(ToStoredResolution)
                            .ToArray();
                        return resolutions;
                    });
            }
            catch (Exception exception)
            {
                operationFailure = exception;
                throw;
            }
            finally
            {
                await DropRequestedConfigurationKeysAsync(connection, operationFailure);
            }
        }, cancellationToken);

    public Task<ConfigurationCommandReceipt> SetAsync(
        ConfigurationEntryIdentity identity,
        string value,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async (connection, transaction) =>
            {
                var entry = await FindEntryAsync(
                    connection,
                    transaction,
                    identity,
                    cancellationToken);

                long entryId;
                long version;
                EnvironmentName storedEnvironment;
                ConfigurationKey storedKey;
                if (entry is null)
                {
                    entryId = await connection.ExecuteScalarAsync<long>(
                        new CommandDefinition(
                            InsertEntrySql,
                            new
                            {
                                Environment = identity.Environment.Value,
                                Key = identity.Key.Value
                            },
                            transaction,
                            cancellationToken: cancellationToken));
                    version = 1;
                    storedEnvironment = identity.Environment;
                    storedKey = identity.Key;
                }
                else
                {
                    var latest = await GetRequiredLatestVersionAsync(
                        connection,
                        transaction,
                        entry.Id,
                        cancellationToken);
                    entryId = entry.Id;
                    version = checked(latest.Version + 1);
                    storedEnvironment = EnvironmentName.FromValidated(entry.Environment);
                    storedKey = ConfigurationKey.FromValidated(entry.ConfigurationKey);
                }

                await InsertVersionAsync(
                    connection,
                    transaction,
                    entryId,
                    version,
                    ConfigurationOperation.Set,
                    value,
                    createdAtUtc,
                    cancellationToken);

                return new ConfigurationCommandReceipt(
                    storedEnvironment,
                    storedKey,
                    version,
                    ConfigurationEntryState.Present,
                    createdAtUtc);
            },
            cancellationToken);

    public Task<RemoveStoreResult> RemoveAsync(
        ConfigurationEntryIdentity identity,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync<RemoveStoreResult>(
            async (connection, transaction) =>
            {
                var entry = await FindEntryAsync(
                    connection,
                    transaction,
                    identity,
                    cancellationToken);
                if (entry is null)
                {
                    return new RemoveStoreResult.NotFound();
                }

                var latest = await GetRequiredLatestVersionAsync(
                    connection,
                    transaction,
                    entry.Id,
                    cancellationToken);
                var latestOperation = ParseOperation(latest.Operation);
                if (latestOperation == ConfigurationOperation.Remove)
                {
                    return new RemoveStoreResult.AlreadyRemoved();
                }

                var version = checked(latest.Version + 1);
                await InsertVersionAsync(
                    connection,
                    transaction,
                    entry.Id,
                    version,
                    ConfigurationOperation.Remove,
                    value: null,
                    createdAtUtc,
                    cancellationToken);

                return new RemoveStoreResult.Created(
                    new ConfigurationCommandReceipt(
                        EnvironmentName.FromValidated(entry.Environment),
                        ConfigurationKey.FromValidated(entry.ConfigurationKey),
                        version,
                        ConfigurationEntryState.Removed,
                        createdAtUtc));
            },
            cancellationToken);

    private Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> operation,
        CancellationToken cancellationToken) =>
        ExecuteWithConnectionAsync(async connection =>
        {
            var transaction = connection.BeginTransaction(deferred: false);
            Exception? operationFailure = null;
            try
            {
                var result = await operation(connection, transaction);
                transaction.Commit();
                return result;
            }
            catch (Exception exception)
            {
                operationFailure = exception;
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // Preserve the operation failure; rollback failure is secondary.
                }

                throw;
            }
            finally
            {
                try
                {
                    transaction.Dispose();
                }
                catch when (operationFailure is not null)
                {
                    // Preserve the operation failure after a failed rollback.
                }
            }
        }, cancellationToken);

    private Task<T> ExecuteReadAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> operation,
        CancellationToken cancellationToken) =>
        ExecuteWithConnectionAsync(
            connection => ExecuteDeferredReadAsync(
                connection,
                transaction => operation(connection, transaction)),
            cancellationToken);

    private Task<T> ExecuteWithConnectionAsync<T>(
        Func<SqliteConnection, Task<T>> operation,
        CancellationToken cancellationToken) =>
        SqliteFailureTranslator.ExecuteAsync(async () =>
        {
            SqliteConnection? connection = null;
            Exception? operationFailure = null;
            try
            {
                connection = await _connections.OpenConnectionAsync(cancellationToken);
                return await operation(connection);
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
        });

    private static async Task<T> ExecuteDeferredReadAsync<T>(
        SqliteConnection connection,
        Func<SqliteTransaction, Task<T>> operation)
    {
        var transaction = connection.BeginTransaction(deferred: true);
        Exception? operationFailure = null;
        try
        {
            var result = await operation(transaction);
            transaction.Commit();
            return result;
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // Preserve the operation failure; rollback failure is secondary.
            }

            throw;
        }
        finally
        {
            try
            {
                transaction.Dispose();
            }
            catch when (operationFailure is not null)
            {
                // Preserve the operation failure after a failed rollback.
            }
        }
    }

    private async Task DropRequestedConfigurationKeysAsync(
        SqliteConnection connection,
        Exception? operationFailure)
    {
        try
        {
            // Request cancellation must not return a connection with a temporary table to the pool; cleanup failure clears that pool.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    DropRequestedConfigurationKeysSql,
                    cancellationToken: CancellationToken.None));
        }
        catch when (operationFailure is not null)
        {
            _connections.ClearConnectionPool(connection);
        }
        catch
        {
            _connections.ClearConnectionPool(connection);
            throw;
        }
    }

    private static Task<StoredConfigurationEntry?> FindEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<StoredConfigurationEntry>(
            new CommandDefinition(
                SelectEntrySql,
                new
                {
                    Environment = identity.Environment.Value,
                    Key = identity.Key.Value
                },
                transaction,
                cancellationToken: cancellationToken));

    private static async Task<StoredLatestVersion> GetRequiredLatestVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long entryId,
        CancellationToken cancellationToken)
    {
        var latest = await connection.QuerySingleOrDefaultAsync<StoredLatestVersion>(
            new CommandDefinition(
                SelectLatestVersionSql,
                new { EntryId = entryId },
                transaction,
                cancellationToken: cancellationToken));

        return latest ?? throw new PersistenceFailureException(
            PersistenceFailureCategory.Other,
            new InvalidDataException("A stored Configuration Entry has no latest version."));
    }

    private static async Task ThrowIfEntryHasNoVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConfigurationEntryIdentity identity,
        CancellationToken cancellationToken)
    {
        var entryExists = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                EntryExistsSql,
                new
                {
                    Environment = identity.Environment.Value,
                    Key = identity.Key.Value
                },
                transaction,
                cancellationToken: cancellationToken));
        if (entryExists != 0)
        {
            throw CorruptStoredShape("A stored Configuration Entry has no versions.");
        }
    }

    private static async Task ThrowIfAnyRequestedEntryHasNoVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EnvironmentName environment,
        CancellationToken cancellationToken)
    {
        var entryWithoutVersionsExists = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                RequestedEntryWithoutVersionsSql,
                new { Environment = environment.Value },
                transaction,
                cancellationToken: cancellationToken));
        if (entryWithoutVersionsExists != 0)
        {
            throw CorruptStoredShape("A requested Configuration Entry has no versions.");
        }
    }

    private static Task<int> InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long entryId,
        long version,
        ConfigurationOperation operation,
        string? value,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                InsertVersionSql,
                new
                {
                    EntryId = entryId,
                    Version = version,
                    Operation = operation.ToString(),
                    Value = value,
                    CreatedAtUtc = createdAtUtc.ToString("O", CultureInfo.InvariantCulture)
                },
                transaction,
                cancellationToken: cancellationToken));

    private static ConfigurationCurrent ToCurrent(StoredReadVersion row)
    {
        var identity = ToIdentity(row);
        var version = ToVersion(row);
        return new ConfigurationCurrent(
            identity.Environment,
            identity.Key,
            version.Version,
            ToState(version.Operation, version.Value),
            version.Value,
            version.CreatedAtUtc);
    }

    private static ConfigurationVersion ToVersion(StoredReadVersion row)
    {
        if (row.Version < 1)
        {
            throw CorruptStoredShape("A stored Entry Version is less than one.");
        }

        var operation = ParseOperation(row.Operation);
        var value = row.Value;
        _ = ToState(operation, value);
        return new ConfigurationVersion(
            row.Version,
            operation,
            value,
            ParseCreatedAtUtc(row.CreatedAtUtc));
    }

    private static ConfigurationEntrySummary ToSummary(StoredEntrySummaryRow row)
    {
        if (row.Version is not { } storedVersion)
        {
            throw CorruptStoredShape("A stored Configuration Entry has no versions.");
        }

        var current = ToCurrent(new StoredReadVersion
        {
            Environment = row.Environment,
            ConfigurationKey = row.ConfigurationKey,
            Version = storedVersion,
            Operation = row.Operation,
            Value = row.Value,
            CreatedAtUtc = row.CreatedAtUtc
        });
        return new ConfigurationEntrySummary(
            current.Key,
            current.Version,
            current.State,
            current.CreatedAtUtc);
    }

    private static StoredResolution ToStoredResolution(StoredResolutionRow row)
    {
        var key = ToKey(row.ConfigurationKey);
        var operation = ParseOperation(row.Operation);
        return new StoredResolution(key, row.Version, ToState(operation, row.Value), row.Value);
    }

    private static ConfigurationEntryIdentity ToIdentity(StoredReadVersion row) => new(
        ToEnvironment(row.Environment),
        ToKey(row.ConfigurationKey));

    private static EnvironmentName ToEnvironment(string? environment)
    {
        if (environment is null)
        {
            throw CorruptStoredShape("A stored Configuration Entry has a null environment.");
        }

        try
        {
            return EnvironmentName.FromValidated(environment);
        }
        catch (ArgumentException exception)
        {
            throw CorruptStoredShape("A stored Configuration Entry has an invalid environment.", exception);
        }
    }

    private static ConfigurationKey ToKey(string? key)
    {
        if (key is null)
        {
            throw CorruptStoredShape("A stored Configuration Entry has a null configuration key.");
        }

        try
        {
            return ConfigurationKey.FromValidated(key);
        }
        catch (ArgumentException exception)
        {
            throw CorruptStoredShape("A stored Configuration Entry has an invalid configuration key.", exception);
        }
    }

    private static DateTimeOffset ParseCreatedAtUtc(string? createdAtUtc)
    {
        if (createdAtUtc is null)
        {
            throw CorruptStoredShape("A stored Entry Version has a null timestamp.");
        }

        try
        {
            return DateTimeOffset.ParseExact(
                createdAtUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }
        catch (FormatException exception)
        {
            throw CorruptStoredShape("A stored Entry Version has an invalid timestamp.", exception);
        }
    }

    private static ConfigurationEntryState ToState(
        ConfigurationOperation operation,
        string? value) => operation switch
        {
            ConfigurationOperation.Set when value is not null => ConfigurationEntryState.Present,
            ConfigurationOperation.Remove when value is null => ConfigurationEntryState.Removed,
            _ => throw CorruptStoredShape("A stored Entry Version has an invalid operation and value combination.")
        };

    private static PersistenceFailureException CorruptStoredShape(
        string message,
        Exception? innerException = null) => new(
            PersistenceFailureCategory.Other,
            innerException ?? new InvalidDataException(message));

    private static ConfigurationOperation ParseOperation(string? operation) => operation switch
    {
        "Set" => ConfigurationOperation.Set,
        "Remove" => ConfigurationOperation.Remove,
        _ => throw CorruptStoredShape("A stored Entry Version has an unknown operation.")
    };
}
