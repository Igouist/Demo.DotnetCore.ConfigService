namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Validation;

public sealed class ManagementValidatorTests
{
    [Fact]
    public void Set_accumulates_independent_invalid_fields_as_contract_errors()
    {
        var result = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest(" ", "CentralConfig:Value", null))
            .ToContractErrors();

        AssertErrorsContain(
            result,
            ("environment", "environment_required", "Environment is required."),
            ("key", "invalid_configuration_key", "Configuration Key has an invalid structure."),
            ("value", "value_required", "Value is required."));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Set_stops_each_dependent_field_chain_after_the_first_failure()
    {
        var result = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest(" ", "", null))
            .ToContractErrors();

        AssertErrorsContain(
            result,
            ("environment", "environment_required", "Environment is required."),
            ("key", "key_required", "Configuration Key is required."),
            ("value", "value_required", "Value is required."));
        Assert.DoesNotContain(result, error => error.Code == "environment_has_outer_whitespace");
        Assert.DoesNotContain(result, error => error.Code == "invalid_configuration_key");
        Assert.DoesNotContain(result, error => error.Code == "value_too_large");
    }

    [Theory]
    [InlineData(null, "environment_required", "Environment is required.")]
    [InlineData(" Production ", "environment_has_outer_whitespace", "Environment must not have leading or trailing whitespace.")]
    public void Remove_validates_environment(string? environment, string code, string message)
    {
        var result = new RemoveConfigurationRequestValidator()
            .Validate(new RemoveConfigurationRequest(environment, "Demo:Setting"))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "environment", code, message);
    }

    [Theory]
    [InlineData(null, "key_required", "Configuration Key is required.")]
    [InlineData("CentralConfig:Setting", "invalid_configuration_key", "Configuration Key has an invalid structure.")]
    public void Query_validates_key(string? key, string code, string message)
    {
        var result = new ConfigurationIdentityQueryValidator()
            .Validate(new ConfigurationIdentityQuery("Production", key))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "key", code, message);
    }

    [Fact]
    public void Set_rejects_value_larger_than_16_kib_in_utf8_bytes()
    {
        var result = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest("Production", "Demo:Setting", new string('界', 5_462)))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "value", "value_too_large", "Value must be at most 16,384 UTF-8 bytes.");
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
