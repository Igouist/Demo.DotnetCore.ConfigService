using Demo.DotnetCore.ConfigService.CentralHost;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCentralConfigHost(
    builder.Configuration,
    builder.Environment.ContentRootPath);

var app = builder.Build();

await app.Services
    .GetRequiredService<SqliteDatabaseInitializer>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

if (string.IsNullOrEmpty(app.Configuration[ManagementApiKeyDefaults.ConfigurationKey]))
{
    app.Logger.LogWarning(
        "No management API key is configured ({ConfigurationKey}); every management request will be rejected with 401.",
        ManagementApiKeyDefaults.ConfigurationKey);
}

app.UseMiddleware<CentralExceptionMiddleware>();
app.UseCentralHostOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapCentralHostReadinessProbe();

await app.RunAsync();

public partial class Program;
