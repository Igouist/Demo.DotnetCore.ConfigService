using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.CentralHost;

public static class CentralHostServiceCollectionExtensions
{
    public static IServiceCollection AddCentralConfigHost(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var databaseOptions = CentralConfigDatabaseOptions.FromConfiguration(configuration, contentRootPath);
        var instanceIdentity = CentralHostInstanceIdentity.FromConfiguration(configuration);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddProblemDetails();
        services.AddSingleton(databaseOptions);
        services.AddSingleton(instanceIdentity);
        services.AddHealthChecks()
            .AddCheck<SqliteReadinessHealthCheck>(
                SqliteReadinessHealthCheck.Name,
                failureStatus: HealthStatus.Unhealthy,
                tags: [SqliteReadinessHealthCheck.ReadinessTag],
                timeout: TimeSpan.FromSeconds(
                    CentralConfigDatabaseOptions.OperationTimeoutSeconds));
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteDatabaseInitializer>();
        services.AddSingleton<IConfigurationEntryStore, SqliteConfigurationEntryStore>();
        services.AddSingleton<IValidator<SetConfigurationRequest>, SetConfigurationRequestValidator>();
        services.AddSingleton<IValidator<RemoveConfigurationRequest>, RemoveConfigurationRequestValidator>();
        services.AddSingleton<IValidator<ConfigurationIdentityQuery>, ConfigurationIdentityQueryValidator>();
        services.AddSingleton<IValidator<EnvironmentQuery>, EnvironmentQueryValidator>();
        services.AddSingleton<IValidator<ResolveConfigurationsRequest>, ResolveConfigurationsRequestValidator>();
        services.AddSingleton<StrictJsonRequestReader>();
        services.AddSingleton<CentralProblemDetailsWriter>();
        services.AddSingleton<ManagementModule>();
        services.AddSingleton<DistributionModule>();
        services.AddSingleton(static provider => new ManagementControllerDependencies(
            provider.GetRequiredService<StrictJsonRequestReader>(),
            provider.GetRequiredService<IValidator<SetConfigurationRequest>>(),
            provider.GetRequiredService<IValidator<RemoveConfigurationRequest>>(),
            provider.GetRequiredService<IValidator<ConfigurationIdentityQuery>>(),
            provider.GetRequiredService<IValidator<EnvironmentQuery>>(),
            provider.GetRequiredService<ManagementModule>()));
        services.AddSingleton(static provider => new DistributionControllerDependencies(
            provider.GetRequiredService<StrictJsonRequestReader>(),
            provider.GetRequiredService<IValidator<ResolveConfigurationsRequest>>(),
            provider.GetRequiredService<DistributionModule>(),
            provider.GetRequiredService<ILogger<DistributionConfigurationsController>>()));

        services.Configure<ApiBehaviorOptions>(options =>
            options.SuppressModelStateInvalidFilter = true);
        services.AddControllers().AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddCentralHostOpenApi();

        return services;
    }
}
