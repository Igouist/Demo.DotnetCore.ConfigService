using Microsoft.Extensions.Hosting;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

internal sealed class TestHostBuilder : IDisposable
{
    private readonly string _contentRoot;

    internal TestHostBuilder(string baseJson, string environmentJson, string hostEnvironment = "UAT")
    {
        _contentRoot = Path.Combine(
            Path.GetTempPath(),
            "Demo.DotnetCore.ConfigService.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), baseJson);
        File.WriteAllText(
            Path.Combine(_contentRoot, $"appsettings.{hostEnvironment}.json"),
            environmentJson);
        Builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = _contentRoot,
            EnvironmentName = hostEnvironment
        });
    }

    internal HostApplicationBuilder Builder { get; }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
