using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed record CentralConfigDatabaseOptions(
    string ConnectionString,
    string DataSourcePath)
{
    public const string ConnectionStringName = "CentralConfigDatabase";
    public const int OperationTimeoutSeconds = 5;

    public static CentralConfigDatabaseOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Central Config database configuration is missing.");
        }

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Central Config database configuration is invalid.");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || StringComparer.Ordinal.Equals(builder.DataSource, ":memory:")
            || builder.Mode == SqliteOpenMode.Memory)
        {
            throw new InvalidOperationException("Central Config database configuration is invalid.");
        }

        string dataSourcePath;
        try
        {
            var candidate = Path.IsPathRooted(builder.DataSource)
                ? builder.DataSource
                : Path.Combine(contentRootPath, builder.DataSource);
            dataSourcePath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("Central Config database configuration is invalid.");
        }

        builder.DataSource = dataSourcePath;
        builder.DefaultTimeout = OperationTimeoutSeconds;
        builder.ForeignKeys = true;

        return new CentralConfigDatabaseOptions(builder.ConnectionString, dataSourcePath);
    }
}
