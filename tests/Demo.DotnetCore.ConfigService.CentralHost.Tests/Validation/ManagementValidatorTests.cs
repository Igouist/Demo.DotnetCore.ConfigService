namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Validation;

public sealed class ManagementValidatorTests
{
    [Fact]
    public void Set_accumulates_independent_invalid_fields_as_contract_errors()
    {
        var result = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest(" ", "CentralConfig:Value", null, " ", null))
            .ToContractErrors();

        AssertErrorsContain(
            result,
            ("environment", "environment_required", "Environment is required."),
            ("key", "invalid_configuration_key", "Configuration Key has an invalid structure."),
            ("value", "value_required", "Value is required."),
            ("changedBy", "changed_by_required", "ChangedBy is required."));
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Set_stops_each_dependent_field_chain_after_the_first_failure()
    {
        var result = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest(" ", "", null, "alice", null))
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
            .Validate(new RemoveConfigurationRequest(environment, "Demo:Setting", "alice", null))
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
            .Validate(new SetConfigurationRequest("Production", "Demo:Setting", new string('界', 5_462), "alice", null))
            .ToContractErrors();

        Assert.Single(result);
        AssertError(result[0], "value", "value_too_large", "Value must be at most 16,384 UTF-8 bytes.");
    }

    [Theory]
    [InlineData(null, "changed_by_required", "ChangedBy is required.")]
    [InlineData("   ", "changed_by_required", "ChangedBy is required.")]
    [InlineData("ali\nce", "changed_by_invalid_characters", "ChangedBy must not contain control characters.")]
    [InlineData("ali\u0000ce", "changed_by_invalid_characters", "ChangedBy must not contain control characters.")]
    public void Set_and_remove_require_a_printable_changed_by(string? changedBy, string code, string message)
    {
        var setResult = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest("Production", "Demo:Setting", "x", changedBy, null))
            .ToContractErrors();
        var removeResult = new RemoveConfigurationRequestValidator()
            .Validate(new RemoveConfigurationRequest("Production", "Demo:Setting", changedBy, null))
            .ToContractErrors();

        AssertError(Assert.Single(setResult), "changedBy", code, message);
        AssertError(Assert.Single(removeResult), "changedBy", code, message);
    }

    [Fact]
    public void Change_attribution_limits_apply_after_trimming()
    {
        string changedByAtLimit = "  " + new string('a', ChangeAttribution.MaxChangedByLength) + "  ";
        string reasonAtLimit = " " + new string('b', ChangeAttribution.MaxReasonLength) + " ";
        var validator = new SetConfigurationRequestValidator();

        Assert.Empty(validator
            .Validate(new SetConfigurationRequest("Production", "Demo:Setting", "x", changedByAtLimit, reasonAtLimit))
            .ToContractErrors());

        var tooLong = validator
            .Validate(new SetConfigurationRequest(
                "Production",
                "Demo:Setting",
                "x",
                new string('a', ChangeAttribution.MaxChangedByLength + 1),
                new string('b', ChangeAttribution.MaxReasonLength + 1)))
            .ToContractErrors();
        AssertErrorsContain(
            tooLong,
            ("changedBy", "changed_by_too_long", "ChangedBy must be at most 100 characters."),
            ("reason", "reason_too_long", "Reason must be at most 500 characters."));
        Assert.Equal(2, tooLong.Count);

        var attribution = ChangeAttribution.FromValidated(changedByAtLimit, reasonAtLimit);
        Assert.Equal(new string('a', ChangeAttribution.MaxChangedByLength), attribution.ChangedBy);
        Assert.Equal(new string('b', ChangeAttribution.MaxReasonLength), attribution.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_reason_is_optional_and_stored_as_null(string? reason)
    {
        Assert.Empty(new RemoveConfigurationRequestValidator()
            .Validate(new RemoveConfigurationRequest("Production", "Demo:Setting", "alice", reason))
            .ToContractErrors());
        Assert.Null(ChangeAttribution.FromValidated("alice", reason).Reason);
    }

    [Fact]
    public void Reason_rejects_control_characters()
    {
        var result = new SetConfigurationRequestValidator()
            .Validate(new SetConfigurationRequest("Production", "Demo:Setting", "x", "alice", "line1\nline2"))
            .ToContractErrors();

        AssertError(Assert.Single(result), "reason", "reason_invalid_characters", "Reason must not contain control characters.");
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
