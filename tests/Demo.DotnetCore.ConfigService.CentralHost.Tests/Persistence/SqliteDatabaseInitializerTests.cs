using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class SqliteDatabaseInitializerTests
{
    private const string IncompatibleMessage = "The Central Config database schema is incompatible.";
    private const string InitializationFailedMessage = "The Central Config database could not be initialized.";

    [Fact]
    public async Task InitializeAsync_creates_schema_version_1_with_constraints_indexes_and_wal()
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config.db");
        var initializer = CreateInitializer(databasePath);

        await initializer.InitializeAsync(CancellationToken.None);

        await using var connection = await OpenConnectionAsync(databasePath);
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('ConfigurationEntries', 'ConfigurationEntryVersions');
                """));

        await connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntries (Id, Environment, ConfigurationKey)
            VALUES (1, 'Production', 'Demo:Message');
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (1, 1, 1, 'Set', 'hello', '2026-08-31T00:00:00Z', 'test-author');
            """);

        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntries (Id, Environment, ConfigurationKey)
            VALUES (2, 'production', 'demo:message');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (2, 1, 1, 'Set', 'duplicate', '2026-08-31T00:00:01Z', 'test-author');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (3, 42, 2, 'Set', 'orphan', '2026-08-31T00:00:02Z', 'test-author');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (4, 1, 0, 'Set', 'invalid-version', '2026-08-31T00:00:03Z', 'test-author');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (5, 1, 2, 'Replace', 'invalid-operation', '2026-08-31T00:00:04Z', 'test-author');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (6, 1, 2, 'Set', NULL, '2026-08-31T00:00:05Z', 'test-author');
            """));
        await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
            INSERT INTO ConfigurationEntryVersions
                (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
            VALUES (7, 1, 2, 'Remove', 'invalid-value', '2026-08-31T00:00:06Z', 'test-author');
            """));

        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<long>("""
                SELECT COUNT(*)
                FROM pragma_index_xinfo('IX_ConfigurationEntryVersions_Latest')
                WHERE key = 1
                  AND ((seqno = 0 AND name = 'ConfigurationEntryId' AND "desc" = 0)
                    OR (seqno = 1 AND name = 'Version' AND "desc" = 1));
                """));
    }

    [Fact]
    public async Task InitializeAsync_accepts_compatible_version_1_without_deleting_rows()
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config.db");
        await CreateVersion1DatabaseAsync(databasePath);
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            await connection.ExecuteAsync("""
                CREATE TABLE UnrelatedData (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
                CREATE INDEX IX_UnrelatedData_Value ON UnrelatedData (Value);
                INSERT INTO UnrelatedData (Id, Value) VALUES (1, 'unrelated-preserved');
                INSERT INTO ConfigurationEntries (Id, Environment, ConfigurationKey)
                VALUES (1, 'Production', 'Demo:Message');
                INSERT INTO ConfigurationEntryVersions
                    (Id, ConfigurationEntryId, Version, Operation, Value, CreatedAtUtc, ChangedBy)
                VALUES (1, 1, 1, 'Set', 'preserved', '2026-08-31T00:00:00Z', 'test-author');
                """);
        }

        await CreateInitializer(databasePath).InitializeAsync(CancellationToken.None);

        await using var verification = await OpenConnectionAsync(databasePath);
        Assert.Equal(
            "preserved",
            await verification.ExecuteScalarAsync<string>("SELECT Value FROM ConfigurationEntryVersions WHERE Id = 1;"));
        Assert.Equal(
            "unrelated-preserved",
            await verification.ExecuteScalarAsync<string>("SELECT Value FROM UnrelatedData WHERE Id = 1;"));
    }

    [Fact]
    public async Task InitializeAsync_rejects_unknown_schema_version_with_safe_message()
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config-secret.db");
        await ExecuteDatabaseSqlAsync(databasePath, "PRAGMA user_version = 2;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateInitializer(databasePath).InitializeAsync(CancellationToken.None));

        AssertSafeMessage(exception, IncompatibleMessage, databasePath);
    }

    [Theory]
    [InlineData("ConfigurationEntries")]
    [InlineData("ConfigurationEntryVersions")]
    [InlineData("configurationentries")]
    [InlineData("CONFIGURATIONENTRYVERSIONS")]
    public async Task InitializeAsync_rejects_version_0_with_existing_poc_table(string tableName)
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config-secret.db");
        await ExecuteDatabaseSqlAsync(databasePath, $"CREATE TABLE {tableName} (Id INTEGER PRIMARY KEY);");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateInitializer(databasePath).InitializeAsync(CancellationToken.None));

        AssertSafeMessage(exception, IncompatibleMessage, databasePath);
    }

    public static TheoryData<string, string> IncompatibleVersion1Schemas => new()
    {
        { "missing required column", Version1Ddl.Replace("    CreatedAtUtc         TEXT NOT NULL,", "", StringComparison.Ordinal) },
        { "schema before POC-063 attribution columns", Version1Ddl.Replace("    ChangedBy            TEXT NOT NULL,\n    Reason               TEXT NULL,\n", "", StringComparison.Ordinal) },
        { "ChangedBy is nullable", Version1Ddl.Replace("ChangedBy            TEXT NOT NULL", "ChangedBy            TEXT NULL", StringComparison.Ordinal) },
        { "required column has wrong type", Version1Ddl.Replace("CreatedAtUtc         TEXT NOT NULL", "CreatedAtUtc         BLOB NOT NULL", StringComparison.Ordinal) },
        { "required column is nullable", Version1Ddl.Replace("ConfigurationEntryId INTEGER NOT NULL", "ConfigurationEntryId INTEGER NULL", StringComparison.Ordinal) },
        { "Id is not the primary key", Version1Ddl.Replace("Id               INTEGER PRIMARY KEY", "Id               INTEGER NOT NULL", StringComparison.Ordinal) },
        { "missing Version check", Version1Ddl.Replace("Version              INTEGER NOT NULL CHECK (Version >= 1)", "Version              INTEGER NOT NULL", StringComparison.Ordinal) },
        { "missing Operation check", Version1Ddl.Replace("Operation            TEXT NOT NULL CHECK (Operation IN ('Set', 'Remove'))", "Operation            TEXT NOT NULL", StringComparison.Ordinal) },
        { "missing Value-state check", Version1Ddl.Replace(ValueCheck, "        CONSTRAINT CK_ConfigurationEntryVersions_Value CHECK (1 = 1)\n", StringComparison.Ordinal) },
        { "Operation checks use lowercase literals", Version1Ddl.Replace("'Set'", "'set'", StringComparison.Ordinal).Replace("'Remove'", "'remove'", StringComparison.Ordinal) },
        { "Version check appears only inside a string", Version1Ddl.Replace("CHECK (Version >= 1)", "CHECK ('CHECK(Version >= 1)' IS NOT NULL)", StringComparison.Ordinal) },
        { "Operation check appears only inside a comment", Version1Ddl.Replace("CHECK (Operation IN ('Set', 'Remove'))", "CHECK (1 = 1) /* CHECK (Operation IN ('Set', 'Remove')) */", StringComparison.Ordinal) },
        { "Value-state check appears only inside a comment", Version1Ddl.Replace(ValueCheck, SpoofedValueCheck, StringComparison.Ordinal) },
        { "missing foreign key", Version1Ddl.Replace(ForeignKey, string.Empty, StringComparison.Ordinal) },
        { "compound foreign key contains the required row", Version1Ddl.Replace(ForeignKey, CompoundForeignKey, StringComparison.Ordinal) },
        { "foreign key targets the wrong column", Version1Ddl.Replace(ForeignKey, WrongTargetForeignKey, StringComparison.Ordinal) },
        { "missing identity uniqueness", Version1Ddl.Replace(IdentityUnique, "        CONSTRAINT UQ_ConfigurationEntries_Identity CHECK (Environment IS NOT NULL)\n", StringComparison.Ordinal) },
        { "identity uniqueness uses wrong collation", Version1Ddl.Replace("UNIQUE (Environment, ConfigurationKey)", "UNIQUE (Environment COLLATE BINARY, ConfigurationKey COLLATE BINARY)", StringComparison.Ordinal) },
        { "missing per-entry version uniqueness", Version1Ddl.Replace(VersionUnique, string.Empty, StringComparison.Ordinal) },
        { "latest index is not descending", Version1Ddl.Replace("(ConfigurationEntryId, Version DESC)", "(ConfigurationEntryId, Version ASC)", StringComparison.Ordinal) },
        { "missing latest index", Version1Ddl.Replace(LatestIndex, string.Empty, StringComparison.Ordinal) }
    };

    [Theory]
    [MemberData(nameof(IncompatibleVersion1Schemas))]
    public async Task InitializeAsync_rejects_incompatible_version_1_shape(
        string _,
        string schema)
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config-secret.db");
        Assert.NotEqual(Version1Ddl, schema);
        await ExecuteDatabaseSqlAsync(databasePath, schema);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateInitializer(databasePath).InitializeAsync(CancellationToken.None));

        AssertSafeMessage(exception, IncompatibleMessage, databasePath);
    }

    [Fact]
    public async Task InitializeAsync_rejects_database_when_wal_cannot_be_established()
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "read-only-secret.db");
        await CreateVersion1DatabaseAsync(databasePath);
        await ExecuteDatabaseSqlAsync(databasePath, "PRAGMA journal_mode=DELETE;");

        var initializer = CreateInitializer(databasePath, SqliteOpenMode.ReadOnly);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeAsync(CancellationToken.None));

        AssertSafeMessage(exception, InitializationFailedMessage, databasePath);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task InitializeAsync_creates_the_immediate_parent_directory_before_opening()
    {
        await using var directory = TemporaryDirectory.Create();
        var parentPath = Path.Combine(directory.Path, "nested", "database");
        var databasePath = Path.Combine(parentPath, "central-config.db");

        await CreateInitializer(databasePath).InitializeAsync(CancellationToken.None);

        Assert.True(Directory.Exists(parentPath));
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public async Task InitializeAsync_primary_connection_disposal_failure_has_the_safe_initialization_message()
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config-secret.db");
        var clearPoolCalls = 0;
        var cleanup = CreateFailingCleanup(() => clearPoolCalls++);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateInitializer(databasePath, cleanup: cleanup).InitializeAsync(CancellationToken.None));

        AssertSafeMessage(exception, InitializationFailedMessage, databasePath);
        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task InitializeAsync_incompatible_schema_stays_incompatible_when_connection_disposal_fails()
    {
        await using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "central-config-secret.db");
        await ExecuteDatabaseSqlAsync(databasePath, "PRAGMA user_version = 2;");
        var clearPoolCalls = 0;
        var cleanup = CreateFailingCleanup(() => clearPoolCalls++);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateInitializer(databasePath, cleanup: cleanup).InitializeAsync(CancellationToken.None));

        AssertSafeMessage(exception, IncompatibleMessage, databasePath);
        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task VerifyReadinessAsync_accepts_compatible_version_1_without_changing_data()
    {
        await using var directory = TemporaryDirectory.Create();
        string databasePath = Path.Combine(directory.Path, "central-config.db");
        await CreateVersion1DatabaseAsync(databasePath);
        await ExecuteDatabaseSqlAsync(
            databasePath,
            """
            INSERT INTO ConfigurationEntries (Id, Environment, ConfigurationKey)
            VALUES (1, 'Production', 'Demo:Message');
            """);

        await CreateInitializer(databasePath).VerifyReadinessAsync(CancellationToken.None);

        await using SqliteConnection verification = await OpenConnectionAsync(databasePath);
        Assert.Equal(
            1,
            await verification.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM ConfigurationEntries WHERE Id = 1;"));
    }

    [Fact]
    public async Task VerifyReadinessAsync_propagates_caller_cancellation()
    {
        await using var directory = TemporaryDirectory.Create();
        string databasePath = Path.Combine(directory.Path, "central-config.db");
        await CreateVersion1DatabaseAsync(databasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateInitializer(databasePath).VerifyReadinessAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static SqliteDatabaseInitializer CreateInitializer(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate,
        SqliteConnectionCleanup? cleanup = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ConnectionString;
        var options = new CentralConfigDatabaseOptions(connectionString, databasePath);
        return new SqliteDatabaseInitializer(options, new SqliteConnectionFactory(options, cleanup));
    }

    private static SqliteConnectionCleanup CreateFailingCleanup(Action onClearPool) => new(
        async connection =>
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("disposal-sentinel");
        },
        connection =>
        {
            onClearPool();
            SqliteConnection.ClearPool(connection);
        });

    private static async Task CreateVersion1DatabaseAsync(string databasePath) =>
        await ExecuteDatabaseSqlAsync(databasePath, Version1Ddl);

    private static async Task ExecuteDatabaseSqlAsync(string databasePath, string sql)
    {
        await using var connection = await OpenConnectionAsync(databasePath);
        await connection.ExecuteAsync(sql);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ConnectionString);
        await connection.OpenAsync();
        connection.CreateCollation(
            SqliteConnectionFactory.OrdinalIgnoreCaseCollation,
            static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        return connection;
    }

    private static void AssertSafeMessage(
        InvalidOperationException exception,
        string expectedMessage,
        string databasePath)
    {
        Assert.Equal(expectedMessage, exception.Message);
        Assert.DoesNotContain(databasePath, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string IdentityUnique = """
            CONSTRAINT UQ_ConfigurationEntries_Identity
                UNIQUE (Environment, ConfigurationKey)
        """;

    private const string ForeignKey = """
            CONSTRAINT FK_ConfigurationEntryVersions_Entry
                FOREIGN KEY (ConfigurationEntryId) REFERENCES ConfigurationEntries (Id),
        """;

    private const string CompoundForeignKey = """
            CONSTRAINT FK_ConfigurationEntryVersions_Entry
                FOREIGN KEY (ConfigurationEntryId, Version) REFERENCES ConfigurationEntries (Id, Environment),
        """;

    private const string WrongTargetForeignKey = """
            CONSTRAINT FK_ConfigurationEntryVersions_Entry
                FOREIGN KEY (ConfigurationEntryId) REFERENCES ConfigurationEntries (Environment),
        """;

    private const string VersionUnique = """
            CONSTRAINT UQ_ConfigurationEntryVersions_Version
                UNIQUE (ConfigurationEntryId, Version),
        """;

    private const string ValueCheck = """
            CONSTRAINT CK_ConfigurationEntryVersions_Value
                CHECK (
                    (Operation = 'Set' AND Value IS NOT NULL) OR
                    (Operation = 'Remove' AND Value IS NULL)
                )
        """;

    private const string SpoofedValueCheck = """
            CONSTRAINT CK_ConfigurationEntryVersions_Value
                CHECK (1 = 1) /* CHECK (
                    (Operation = 'Set' AND Value IS NOT NULL) OR
                    (Operation = 'Remove' AND Value IS NULL)
                ) */
        """;

    private const string LatestIndex = """
        CREATE INDEX IX_ConfigurationEntryVersions_Latest
            ON ConfigurationEntryVersions (ConfigurationEntryId, Version DESC);

        """;

    private const string Version1Ddl = """
        CREATE TABLE ConfigurationEntries (
            Id               INTEGER PRIMARY KEY,
            Environment      TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE,
            ConfigurationKey TEXT NOT NULL COLLATE ORDINAL_IGNORE_CASE,
            CONSTRAINT UQ_ConfigurationEntries_Identity
                UNIQUE (Environment, ConfigurationKey)
        );

        CREATE TABLE ConfigurationEntryVersions (
            Id                   INTEGER PRIMARY KEY,
            ConfigurationEntryId INTEGER NOT NULL,
            Version              INTEGER NOT NULL CHECK (Version >= 1),
            Operation            TEXT NOT NULL CHECK (Operation IN ('Set', 'Remove')),
            Value                TEXT NULL,
            CreatedAtUtc         TEXT NOT NULL,
            ChangedBy            TEXT NOT NULL,
            Reason               TEXT NULL,
            CONSTRAINT FK_ConfigurationEntryVersions_Entry
                FOREIGN KEY (ConfigurationEntryId) REFERENCES ConfigurationEntries (Id),
            CONSTRAINT UQ_ConfigurationEntryVersions_Version
                UNIQUE (ConfigurationEntryId, Version),
            CONSTRAINT CK_ConfigurationEntryVersions_Value
                CHECK (
                    (Operation = 'Set' AND Value IS NOT NULL) OR
                    (Operation = 'Remove' AND Value IS NULL)
                )
        );

        CREATE INDEX IX_ConfigurationEntryVersions_Latest
            ON ConfigurationEntryVersions (ConfigurationEntryId, Version DESC);

        PRAGMA user_version = 1;
        """;
}
