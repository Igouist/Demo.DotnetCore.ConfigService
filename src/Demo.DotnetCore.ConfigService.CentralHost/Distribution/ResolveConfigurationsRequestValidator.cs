using FluentValidation;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class ResolveConfigurationsRequestValidator : AbstractValidator<ResolveConfigurationsRequest>
{
    public ResolveConfigurationsRequestValidator()
    {
        SetConfigurationRequestValidator.AddEnvironmentRules(RuleFor(request => request.Environment));

        RuleFor(request => request.Keys)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .WithName("keys")
            .WithErrorCode("keys_required")
            .WithMessage("Keys are required.")
            .Must(keys => keys!.Length > 0)
            .WithName("keys")
            .WithErrorCode("keys_empty")
            .WithMessage("Keys must contain at least one Configuration Key.");

        RuleForEach(request => request.Keys!)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithName("key")
            .WithErrorCode("key_required")
            .WithMessage("Configuration Key is required.")
            .Must(value => string.IsNullOrWhiteSpace(value) || ConfigurationKey.TryCreate(value, out _))
            .WithName("key")
            .WithErrorCode("invalid_configuration_key")
            .WithMessage("Configuration Key has an invalid structure.")
            .When(request => request.Keys is not null);

        RuleFor(request => request.Keys)
            .Must(keys => keys!.Distinct(StringComparer.OrdinalIgnoreCase).Count() == keys!.Length)
            .WithName("keys")
            .WithErrorCode("duplicate_configuration_key")
            .WithMessage("Keys must not contain case-insensitive duplicates.")
            .When(request => request.Keys is { Length: > 0 } && request.Keys!.All(value => ConfigurationKey.TryCreate(value, out _)));
    }
}
