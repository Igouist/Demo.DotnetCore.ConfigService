using Demo.DotnetCore.ConfigService.CentralHost;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCentralConfigHost(
    builder.Configuration,
    builder.Environment.ContentRootPath);

var app = builder.Build();

await app.Services
    .GetRequiredService<SqliteDatabaseInitializer>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

app.UseMiddleware<CentralExceptionMiddleware>();
app.UseCentralHostOpenApi();
app.MapControllers();
app.MapCentralHostReadinessProbe();

await app.RunAsync();

public partial class Program;
