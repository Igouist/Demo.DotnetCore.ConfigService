namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class SqliteSchema
{
    public const string CreateVersion1 = """
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
