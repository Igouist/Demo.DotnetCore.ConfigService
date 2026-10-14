using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Health;

public sealed class SqliteReadinessHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_returns_healthy_for_a_compatible_version_1_database()
    {
        await using var directory = TemporaryDirectory.Create();
        string databasePath = Path.Combine(directory.Path, "central-config.db");
        SqliteDatabaseInitializer initializer = CreateInitializer(databasePath);
        await initializer.InitializeAsync(CancellationToken.None);
        var check = new SqliteReadinessHealthCheck(initializer);

        HealthCheckResult result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Null(result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task CheckHealthAsync_returns_unhealthy_without_dependency_details()
    {
        await using var directory = TemporaryDirectory.Create();
        string databasePath = Path.Combine(directory.Path, "central-config-secret.db");
        SqliteDatabaseInitializer initializer = CreateInitializer(databasePath);
        await initializer.InitializeAsync(CancellationToken.None);
        await using (var connection = new SqliteConnection(CreateConnectionString(databasePath)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("PRAGMA user_version = 2;");
        }

        var check = new SqliteReadinessHealthCheck(initializer);
        HealthCheckResult result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task CheckHealthAsync_rethrows_caller_cancellation()
    {
        await using var directory = TemporaryDirectory.Create();
        string databasePath = Path.Combine(directory.Path, "central-config.db");
        SqliteDatabaseInitializer initializer = CreateInitializer(databasePath);
        await initializer.InitializeAsync(CancellationToken.None);
        var check = new SqliteReadinessHealthCheck(initializer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static SqliteDatabaseInitializer CreateInitializer(string databasePath)
    {
        var options = new CentralConfigDatabaseOptions(
            CreateConnectionString(databasePath),
            databasePath);
        return new SqliteDatabaseInitializer(options, new SqliteConnectionFactory(options));
    }

    private static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = CentralConfigDatabaseOptions.OperationTimeoutSeconds
        }.ConnectionString;
}
