namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Validation;

public sealed class ResolveConfigurationsRequestValidatorTests
{
    [Theory]
    [InlineData(null, "keys_required", "Keys are required.")]
    [InlineData(new string[0], "keys_empty", "Keys must contain at least one Configuration Key.")]
    public void Resolve_requires_a_non_empty_key_list(string[]? keys, string code, string message)
    {
        var result = new ResolveConfigurationsRequestValidator()
            .Validate(new ResolveConfigurationsRequest("Production", keys))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "keys", code, message);
    }

    [Fact]
    public void Resolve_accumulates_environment_and_indexed_key_errors_without_ordering_them()
    {
        var result = new ResolveConfigurationsRequestValidator()
            .Validate(new ResolveConfigurationsRequest(" ", [null, "CentralConfig:Setting"]))
            .ToContractErrors();

        AssertErrorsContain(
            result,
            ("environment", "environment_required", "Environment is required."),
            ("keys[0]", "key_required", "Configuration Key is required."),
            ("keys[1]", "invalid_configuration_key", "Configuration Key has an invalid structure."));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Resolve_stops_each_indexed_key_chain_after_the_first_failure()
    {
        var result = new ResolveConfigurationsRequestValidator()
            .Validate(new ResolveConfigurationsRequest("Production", ["", "Demo:Setting"]))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "keys[0]", "key_required", "Configuration Key is required.");
    }

    [Fact]
    public void Resolve_rejects_ordinal_case_insensitive_duplicate_keys()
    {
        var result = new ResolveConfigurationsRequestValidator()
            .Validate(new ResolveConfigurationsRequest("Production", ["Demo:Setting", "demo:setting"]))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "keys", "duplicate_configuration_key", "Keys must not contain case-insensitive duplicates.");
    }

    [Fact]
    public void Resolve_does_not_run_duplicate_detection_until_every_key_is_valid()
    {
        var result = new ResolveConfigurationsRequestValidator()
            .Validate(new ResolveConfigurationsRequest("Production", ["Demo:Setting", "demo:setting", "CentralConfig:Secret"]))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "keys[2]", "invalid_configuration_key", "Configuration Key has an invalid structure.");
    }

    private static void AssertErrorsContain(
        IReadOnlyList<ProblemDetailsError> errors,
        params (string Path, string Code, string Message)[] expectedErrors)
    {
        foreach (var expected in expectedErrors)
        {
            Assert.Contains(errors, error =>
                error.Path == expected.Path &&
                error.Code == expected.Code &&
                error.Message == expected.Message);
        }
    }

    private static void AssertError(ProblemDetailsError actual, string path, string code, string message)
    {
        Assert.Equal(path, actual.Path);
        Assert.Equal(code, actual.Code);
        Assert.Equal(message, actual.Message);
    }
}
