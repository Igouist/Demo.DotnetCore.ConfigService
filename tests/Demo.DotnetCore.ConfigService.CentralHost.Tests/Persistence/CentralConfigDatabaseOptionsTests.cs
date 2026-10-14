using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Persistence;

public sealed class CentralConfigDatabaseOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfiguration_rejects_missing_connection_string(string? connectionString)
    {
        var configuration = BuildConfiguration(connectionString);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CentralConfigDatabaseOptions.FromConfiguration(configuration, "content-root"));

        Assert.Equal("Central Config database configuration is missing.", exception.Message);
    }

    [Theory]
    [InlineData("Data Source=")]
    [InlineData("Data Source=   ")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=central.db;Mode=Memory")]
    public void FromConfiguration_rejects_non_file_data_sources(string connectionString)
    {
        var configuration = BuildConfiguration(connectionString);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CentralConfigDatabaseOptions.FromConfiguration(configuration, "content-root"));

        Assert.Equal("Central Config database configuration is invalid.", exception.Message);
    }

    [Fact]
    public void FromConfiguration_replaces_malformed_connection_string_details_with_safe_message()
    {
        const string connectionString = "Data Source=central.db;Mode=NotARealMode";
        var configuration = BuildConfiguration(connectionString);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CentralConfigDatabaseOptions.FromConfiguration(configuration, "content-root"));

        Assert.Equal("Central Config database configuration is invalid.", exception.Message);
        Assert.DoesNotContain(connectionString, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_replaces_invalid_path_details_with_safe_message()
    {
        const string path = "central\0config.db";
        var configuration = BuildConfiguration($"Data Source={path}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CentralConfigDatabaseOptions.FromConfiguration(configuration, "content-root"));

        Assert.Equal("Central Config database configuration is invalid.", exception.Message);
        Assert.DoesNotContain(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_resolves_relative_data_source_against_content_root()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "central-host-content-root");
        var configuration = BuildConfiguration("Data Source=data/central.db");

        var options = CentralConfigDatabaseOptions.FromConfiguration(configuration, contentRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "data/central.db")), options.DataSourcePath);
        Assert.Equal(options.DataSourcePath, new SqliteConnectionStringBuilder(options.ConnectionString).DataSource);
    }

    [Fact]
    public void FromConfiguration_keeps_absolute_data_source_absolute()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "central-host-content-root");
        var dataSource = Path.Combine(Path.GetTempPath(), "central-host-absolute.db");
        var configuration = BuildConfiguration($"Data Source={dataSource}");

        var options = CentralConfigDatabaseOptions.FromConfiguration(configuration, contentRoot);

        Assert.Equal(Path.GetFullPath(dataSource), options.DataSourcePath);
    }

    [Fact]
    public void FromConfiguration_enforces_foreign_keys_and_timeout_while_preserving_cache_and_pooling()
    {
        var configuration = BuildConfiguration("Data Source=data/central.db;Cache=Shared;Pooling=False");

        var options = CentralConfigDatabaseOptions.FromConfiguration(configuration, "content-root");
        var builder = new SqliteConnectionStringBuilder(options.ConnectionString);

        Assert.Equal(CentralConfigDatabaseOptions.OperationTimeoutSeconds, builder.DefaultTimeout);
        Assert.True(builder.ForeignKeys);
        Assert.Equal(SqliteCacheMode.Shared, builder.Cache);
        Assert.False(builder.Pooling);
    }

    private static IConfiguration BuildConfiguration(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{CentralConfigDatabaseOptions.ConnectionStringName}"] = connectionString
            })
            .Build();
}
