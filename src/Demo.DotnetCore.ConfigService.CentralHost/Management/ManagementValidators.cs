using FluentValidation;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class SetConfigurationRequestValidator : AbstractValidator<SetConfigurationRequest>
{
    public SetConfigurationRequestValidator()
    {
        AddEnvironmentRules(RuleFor(request => request.Environment));
        AddKeyRules(RuleFor(request => request.Key));

        RuleFor(request => request.Value)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .WithName("value")
            .WithErrorCode("value_required")
            .WithMessage("Value is required.")
            .Must(ConfigurationValueLimits.IsValid)
            .WithName("value")
            .WithErrorCode("value_too_large")
            .WithMessage("Value must be at most 16,384 UTF-8 bytes.");
        AddChangeAttributionRules(this, request => request.ChangedBy, request => request.Reason);
    }

    internal static void AddChangeAttributionRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, string?>> changedBy,
        System.Linq.Expressions.Expression<Func<T, string?>> reason)
    {
        validator.RuleFor(changedBy)
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("changedBy")
            .WithErrorCode("changed_by_required")
            .WithMessage("ChangedBy is required.")
            .Must(ChangeAttribution.HasNoControlCharacters)
            .WithName("changedBy")
            .WithErrorCode("changed_by_invalid_characters")
            .WithMessage("ChangedBy must not contain control characters.")
            .Must(ChangeAttribution.IsValidChangedBy)
            .WithName("changedBy")
            .WithErrorCode("changed_by_too_long")
            .WithMessage("ChangedBy must be at most 100 characters.");

        validator.RuleFor(reason)
            .Cascade(CascadeMode.Stop)
            .Must(ChangeAttribution.HasNoControlCharacters)
            .WithName("reason")
            .WithErrorCode("reason_invalid_characters")
            .WithMessage("Reason must not contain control characters.")
            .Must(ChangeAttribution.IsValidReason)
            .WithName("reason")
            .WithErrorCode("reason_too_long")
            .WithMessage("Reason must be at most 500 characters.");
    }

    internal static void AddEnvironmentRules<T>(IRuleBuilderInitial<T, string?> ruleBuilder) =>
        ruleBuilder
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("environment")
            .WithErrorCode("environment_required")
            .WithMessage("Environment is required.")
            .Must(value => value == value!.Trim())
            .WithName("environment")
            .WithErrorCode("environment_has_outer_whitespace")
            .WithMessage("Environment must not have leading or trailing whitespace.");

    internal static void AddKeyRules<T>(IRuleBuilderInitial<T, string?> ruleBuilder) =>
        ruleBuilder
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("key")
            .WithErrorCode("key_required")
            .WithMessage("Configuration Key is required.")
            .Must(value => ConfigurationKey.TryCreate(value, out _))
            .WithName("key")
            .WithErrorCode("invalid_configuration_key")
            .WithMessage("Configuration Key has an invalid structure.");
}

internal sealed class RemoveConfigurationRequestValidator : AbstractValidator<RemoveConfigurationRequest>
{
    public RemoveConfigurationRequestValidator()
    {
        SetConfigurationRequestValidator.AddEnvironmentRules(RuleFor(request => request.Environment));
        SetConfigurationRequestValidator.AddKeyRules(RuleFor(request => request.Key));
        SetConfigurationRequestValidator.AddChangeAttributionRules(
            this,
            request => request.ChangedBy,
            request => request.Reason);
    }
}

internal sealed class ConfigurationIdentityQueryValidator : AbstractValidator<ConfigurationIdentityQuery>
{
    public ConfigurationIdentityQueryValidator()
    {
        SetConfigurationRequestValidator.AddEnvironmentRules(RuleFor(request => request.Environment));
        SetConfigurationRequestValidator.AddKeyRules(RuleFor(request => request.Key));
    }
}

internal sealed class EnvironmentQueryValidator : AbstractValidator<EnvironmentQuery>
{
    public EnvironmentQueryValidator() =>
        SetConfigurationRequestValidator.AddEnvironmentRules(RuleFor(request => request.Environment));
}
