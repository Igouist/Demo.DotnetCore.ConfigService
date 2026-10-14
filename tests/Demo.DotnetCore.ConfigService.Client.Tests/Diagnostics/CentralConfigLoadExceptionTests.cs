using System.Net;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Diagnostics;

public sealed class CentralConfigLoadExceptionTests
{
    [Fact]
    public void Public_contract_preserves_safe_typed_fields()
    {
        var exception = new CentralConfigLoadException(
            CentralConfigFailureKind.MissingRequiredKeys,
            attemptCount: 2,
            elapsed: TimeSpan.FromSeconds(3),
            httpStatusCode: HttpStatusCode.OK,
            environment: "UAT",
            missingRequiredKeys: ["Demo:Required", "Demo:Second"]);

        Assert.Equal(CentralConfigFailureKind.MissingRequiredKeys, exception.FailureKind);
        Assert.Equal(2, exception.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(3), exception.Elapsed);
        Assert.Equal(HttpStatusCode.OK, exception.HttpStatusCode);
        Assert.Equal("UAT", exception.Environment);
        Assert.Equal(["Demo:Required", "Demo:Second"], exception.MissingRequiredKeys);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("Demo:Required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_keys_is_never_null_and_is_defensively_copied()
    {
        string[] source = ["Demo:Required"];
        var exception = new CentralConfigLoadException(
            CentralConfigFailureKind.MissingRequiredKeys,
            1,
            TimeSpan.Zero,
            null,
            "UAT",
            source);

        source[0] = "changed";

        Assert.Equal(["Demo:Required"], exception.MissingRequiredKeys);
    }

    [Fact]
    public void Enum_and_exception_have_no_unaccepted_public_construction_surface()
    {
        Assert.Equal(
            [
                "InvalidBootstrapConfiguration",
                "InvalidConfigurationPipeline",
                "TransportFailure",
                "AttemptTimeout",
                "TotalTimeout",
                "Unauthorized",
                "HttpFailure",
                "InvalidResponse",
                "MissingRequiredKeys"
            ],
            Enum.GetNames<CentralConfigFailureKind>());
        Assert.Empty(typeof(CentralConfigLoadException).GetConstructors());
    }

    [Theory]
    [InlineData(
        CentralConfigFailureKind.InvalidBootstrapConfiguration,
        "Central configuration bootstrap is invalid.")]
    [InlineData(
        CentralConfigFailureKind.InvalidConfigurationPipeline,
        "The configuration pipeline cannot accept the central snapshot.")]
    [InlineData(
        CentralConfigFailureKind.TransportFailure,
        "Central configuration could not be reached.")]
    [InlineData(
        CentralConfigFailureKind.AttemptTimeout,
        "A central configuration request timed out.")]
    [InlineData(
        CentralConfigFailureKind.TotalTimeout,
        "Central configuration exceeded its startup time budget.")]
    [InlineData(
        CentralConfigFailureKind.Unauthorized,
        "Central configuration access was not authorized.")]
    [InlineData(
        CentralConfigFailureKind.HttpFailure,
        "Central configuration returned an unsuccessful HTTP status.")]
    [InlineData(
        CentralConfigFailureKind.InvalidResponse,
        "Central configuration returned an invalid response.")]
    [InlineData(
        CentralConfigFailureKind.MissingRequiredKeys,
        "Central configuration is missing required keys.")]
    public void Every_public_failure_kind_has_its_exact_safe_message(
        CentralConfigFailureKind failureKind,
        string expectedMessage)
    {
        var exception = new CentralConfigLoadException(
            failureKind,
            1,
            TimeSpan.Zero,
            null,
            "UAT");

        Assert.Equal(expectedMessage, exception.Message);
    }
}
