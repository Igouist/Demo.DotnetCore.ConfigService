using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class SqliteReadinessHealthCheck
    : IHealthCheck
{
    public const string Name = "central-config-database";
    public const string ReadinessTag = "ready";
    private readonly SqliteDatabaseInitializer databaseInitializer;

    public SqliteReadinessHealthCheck(SqliteDatabaseInitializer databaseInitializer)
    {
        this.databaseInitializer = databaseInitializer;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await databaseInitializer.VerifyReadinessAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy();
        }
    }
}
