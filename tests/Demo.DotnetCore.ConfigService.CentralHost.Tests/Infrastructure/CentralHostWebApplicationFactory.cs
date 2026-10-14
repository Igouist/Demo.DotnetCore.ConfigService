using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

internal sealed class CentralHostWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string InstanceId = "794c1ca58c1342a6863d81e152777b17";

    private readonly TemporaryDirectory _databaseDirectory = TemporaryDirectory.Create();
    private bool _databaseDirectoryDisposed;

    public CentralHostWebApplicationFactory()
    {
        DatabasePath = Path.Combine(_databaseDirectory.Path, "central-config.db");
        TimeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero));
    }

    public string DatabasePath { get; }

    public ManualTimeProvider TimeProvider { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = $"Data Source={DatabasePath};Pooling=False";

        builder.UseSetting("ConnectionStrings:CentralConfigDatabase", connectionString);
        builder.UseSetting(CentralHostInstanceIdentity.ConfigurationKey, InstanceId);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CentralConfigDatabase"] = connectionString,
                [CentralHostInstanceIdentity.ConfigurationKey] = InstanceId
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && !_databaseDirectoryDisposed)
        {
            _databaseDirectory.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _databaseDirectoryDisposed = true;
        }
    }
}
