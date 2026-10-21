using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class JsonRequestBodyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var attribute = context.MethodInfo.GetCustomAttribute<JsonRequestBodyAttribute>();
        if (attribute is null)
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = context.SchemaGenerator.GenerateSchema(
                        attribute.RequestType,
                        context.SchemaRepository),
                    Example = JsonNode.Parse(attribute.Example)
                }
            }
        };
    }
}
