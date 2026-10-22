using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Demo.DotnetCore.ConfigService.CentralHost;

// Marks only the operations behind the management policy, so Swagger UI sends X-Api-Key there and nowhere else.
internal sealed class ManagementApiKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var requiresKey = context.MethodInfo.DeclaringType?
            .GetCustomAttributes<AuthorizeAttribute>()
            .Concat(context.MethodInfo.GetCustomAttributes<AuthorizeAttribute>())
            .Any(static attribute => attribute.Policy == ManagementApiKeyDefaults.Policy) ?? false;
        if (!requiresKey)
        {
            return;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ManagementApiKeyDefaults.Scheme, context.Document)] = []
        });
    }
}
