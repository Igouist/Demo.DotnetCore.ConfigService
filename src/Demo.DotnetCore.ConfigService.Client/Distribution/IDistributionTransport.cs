namespace Demo.DotnetCore.ConfigService.Client;

internal interface IDistributionTransport
{
    Task<DistributionHttpResponse> ResolveAsync(
        Uri endpoint,
        DistributionResolveRequest request,
        CancellationToken cancellationToken);
}
