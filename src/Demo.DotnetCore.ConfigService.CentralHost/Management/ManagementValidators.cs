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
