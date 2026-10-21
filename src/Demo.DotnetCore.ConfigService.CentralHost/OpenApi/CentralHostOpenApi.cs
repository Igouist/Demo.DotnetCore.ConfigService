using Microsoft.OpenApi;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class CentralHostOpenApi
{
    public const string DocumentName = "v1";
    public const string DocumentRoute = "/swagger/v1/swagger.json";
    public const string UiRoute = "/swagger";

    public static IServiceCollection AddCentralHostOpenApi(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "Central Config Host",
                Version = DocumentName,
                Description =
                    "Management 與 Distribution 介面。只在隔離環境使用非敏感資料；" +
                    "錯誤回應為 Problem Details，`code` 為穩定的錯誤代碼，`traceId` 可對應 Central 日誌。"
            });
            options.EnableAnnotations();
            options.SupportNonNullableReferenceTypes();
            options.OperationFilter<JsonRequestBodyOperationFilter>();
        });

        return services;
    }

    public static WebApplication UseCentralHostOpenApi(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
            options.SwaggerEndpoint(DocumentRoute, "Central Config Host v1"));

        return app;
    }
}
