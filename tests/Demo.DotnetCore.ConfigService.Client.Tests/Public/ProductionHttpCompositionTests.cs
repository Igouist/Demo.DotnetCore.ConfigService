using System.Net;
using Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Public;

public sealed class ProductionHttpCompositionTests
{
    [Fact]
    public async Task Production_client_returns_raw_redirect_without_following_it()
    {
        await using var redirectTarget = new ScriptedLoopbackServer(
            new LoopbackResponse(
                HttpStatusCode.OK,
                "{\"values\":{\"Demo:Required\":\"redirected\"},\"missingKeys\":[]}"));
        await using var origin = new ScriptedLoopbackServer(
            new LoopbackResponse(
                HttpStatusCode.Redirect,
                Headers: new Dictionary<string, string>
                {
                    ["Location"] = redirectTarget.BaseAddress.AbsoluteUri
                }));
        using var host = EnabledHost(origin.BaseAddress);
        int sourceCount = host.Builder.Configuration.Sources.Count;

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            host.Builder.AddCentralConfigAsync());

        Assert.Equal(CentralConfigFailureKind.HttpFailure, exception.FailureKind);
        Assert.Equal(HttpStatusCode.Redirect, exception.HttpStatusCode);
        Assert.Single(origin.Requests);
        Assert.Empty(redirectTarget.Requests);
        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
    }

    [Fact]
    public async Task Production_client_does_not_retain_server_cookies_between_loads()
    {
        const string cookieMarker = "central-session=must-not-return-8f93";
        await using var server = new ScriptedLoopbackServer(
            new LoopbackResponse(
                HttpStatusCode.BadRequest,
                Headers: new Dictionary<string, string>
                {
                    ["Set-Cookie"] = $"{cookieMarker}; Path=/"
                }),
            new LoopbackResponse(HttpStatusCode.BadRequest));
        using var firstHost = EnabledHost(server.BaseAddress);
        using var secondHost = EnabledHost(server.BaseAddress);

        _ = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            firstHost.Builder.AddCentralConfigAsync());
        _ = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            secondHost.Builder.AddCentralConfigAsync());

        string? secondRequest = null;
        Assert.Collection(
            server.Requests,
            _ => { },
            request => secondRequest = request);
        Assert.NotNull(secondRequest);
        Assert.DoesNotContain(cookieMarker, secondRequest, StringComparison.Ordinal);
    }

    private static TestHostBuilder EnabledHost(Uri baseAddress) => new(
        $$"""
        {
          "CentralConfig": {
            "Enabled": true,
            "BaseAddress": "{{baseAddress.AbsoluteUri}}gateway",
            "Environment": "UAT",
            "CentralRequiredKeys": [ "Demo:Required" ],
            "FallbackAllowedKeys": []
          }
        }
        """,
        "{}");
}
