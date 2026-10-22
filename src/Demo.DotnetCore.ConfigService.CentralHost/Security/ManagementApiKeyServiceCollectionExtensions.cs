using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class ManagementApiKeyServiceCollectionExtensions
{
    public static IServiceCollection AddManagementApiKey(this IServiceCollection services, IConfiguration configuration)
    {
        var apiKey = configuration[ManagementApiKeyDefaults.ConfigurationKey];

        services
            .AddAuthentication(ManagementApiKeyDefaults.Scheme)
            .AddScheme<ManagementApiKeyOptions, ManagementApiKeyHandler>(
                ManagementApiKeyDefaults.Scheme,
                options => options.ApiKey = apiKey);
        services.AddAuthorizationBuilder()
            .AddPolicy(ManagementApiKeyDefaults.Policy, policy => policy
                .AddAuthenticationSchemes(ManagementApiKeyDefaults.Scheme)
                .RequireAuthenticatedUser());

        return services;
    }
}
