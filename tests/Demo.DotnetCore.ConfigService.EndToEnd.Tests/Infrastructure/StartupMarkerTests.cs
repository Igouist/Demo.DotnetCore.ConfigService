namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class StartupMarkerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"source\":\"another-component\",\"event\":\"failed\"}")]
    [InlineData("{\"source\":\"demo-central-config\"}")]
    [InlineData("{\"source\":\"demo-central-config\",\"event\":17}")]
    [InlineData("info: {\"source\":\"demo-central-config\",\"event\":\"failed\"}")]
    public void TryParse_ignores_malformed_or_non_demo_lines(string line)
    {
        bool parsed = StartupMarker.TryParse(line, out StartupMarker? marker);

        Assert.False(parsed);
        Assert.Null(marker);
    }

    [Fact]
    public void TryParse_reads_only_the_safe_demo_marker_fields()
    {
        const string line = """
            {
              "source": "demo-central-config",
              "event": "failed",
              "valueCount": 7,
              "missingFallbackKeyCount": 2,
              "attemptCount": 3,
              "failureKind": "MissingRequiredKeys",
              "httpStatusCode": 503,
              "delayMs": 125.5,
              "elapsedMs": 250.25,
              "missingRequiredKeys": [ "Demo:CentralRequiredMessage" ],
              "configurationValue": "must-not-be-exposed"
            }
            """;

        bool parsed = StartupMarker.TryParse(line, out StartupMarker? marker);

        Assert.True(parsed);
        Assert.Equal(new[] { "Demo:CentralRequiredMessage" }, marker!.MissingRequiredKeys);
        Assert.Equal(
            new StartupMarker(
                "failed",
                7,
                2,
                3,
                "MissingRequiredKeys",
                503,
                125.5,
                250.25),
            marker with { MissingRequiredKeys = null });
    }

    [Fact]
    public void TryParse_treats_malformed_optional_fields_as_absent()
    {
        const string line = """
            {
              "source": "demo-central-config",
              "event": "retry",
              "attemptCount": "not-a-number",
              "failureKind": 42,
              "httpStatusCode": 2147483648,
              "delayMs": "not-a-number",
              "elapsedMs": null,
              "missingRequiredKeys": [ 1 ]
            }
            """;

        bool parsed = StartupMarker.TryParse(line, out StartupMarker? marker);

        Assert.True(parsed);
        Assert.Equal(
            new StartupMarker("retry", null, null, null, null, null, null, null),
            marker);
    }
}
