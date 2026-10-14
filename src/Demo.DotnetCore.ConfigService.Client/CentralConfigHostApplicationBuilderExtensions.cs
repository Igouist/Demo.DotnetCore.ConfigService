using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client;

public static class CentralConfigHostApplicationBuilderExtensions
{
    private static readonly HttpClient DistributionHttpClient =
        DistributionHttpClientFactory.Create();

    public static Task AddCentralConfigAsync(
        this IHostApplicationBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return CreateRegistration().RegisterAsync(builder, null, cancellationToken);
    }

    public static Task AddCentralConfigAsync(
        this IHostApplicationBuilder builder,
        ILogger startupLogger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(startupLogger);
        return CreateRegistration().RegisterAsync(builder, startupLogger, cancellationToken);
    }

    private static CentralConfigRegistration CreateRegistration() =>
        new(
            new HttpClientDistributionTransport(DistributionHttpClient),
            TimeProvider.System,
            RandomRetryJitter.Instance,
            DistributionResponseParser.Instance,
            SnapshotSourcePreparer.Instance,
            SnapshotCommitter.Instance);
}
