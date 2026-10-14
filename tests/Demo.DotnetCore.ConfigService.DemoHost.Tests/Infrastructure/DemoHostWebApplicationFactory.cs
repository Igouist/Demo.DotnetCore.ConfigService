using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.DemoHost.Tests.Infrastructure;

internal sealed class DemoHostWebApplicationFactory(
    IReadOnlyDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                overrides ?? new Dictionary<string, string?>()));
    }
}
