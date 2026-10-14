namespace Demo.DotnetCore.ConfigService.Client;

internal static class DistributionHttpClientFactory
{
    internal static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            UseDefaultCredentials = false
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
