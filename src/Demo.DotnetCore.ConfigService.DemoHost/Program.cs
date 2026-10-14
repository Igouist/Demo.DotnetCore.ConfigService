using Demo.DotnetCore.ConfigService.Client;
using Demo.DotnetCore.ConfigService.DemoHost;

var builder = WebApplication.CreateBuilder(args);
var startupLogger = new StartupConsoleLogger(Console.Error);

await builder.AddCentralConfigAsync(startupLogger);

builder.Services.AddSingleton<EffectiveConfigurationReader>();
builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

await app.RunAsync();

public partial class Program;
