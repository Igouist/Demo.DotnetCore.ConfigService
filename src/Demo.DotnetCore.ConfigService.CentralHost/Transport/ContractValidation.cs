using FluentValidation;
using FluentValidation.Results;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed record ProblemDetailsError(string Path, string Code, string Message);

internal static class ContractValidation
{
    public static async Task ValidateForContractAsync<T>(
        this IValidator<T> validator,
        T instance,
        CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken);
        if (!result.IsValid)
        {
            throw new RequestValidationException(result.ToContractErrors());
        }
    }

    public static IReadOnlyList<ProblemDetailsError> ToContractErrors(this ValidationResult result) =>
        result.Errors
            .Select(failure => new ProblemDetailsError(
                ToCamelCasePath(failure.PropertyName),
                failure.ErrorCode,
                failure.ErrorMessage))
            .ToArray();

    private static string ToCamelCasePath(string propertyName)
    {
        var bracketIndex = propertyName.IndexOf('[', StringComparison.Ordinal);
        var nameLength = bracketIndex < 0 ? propertyName.Length : bracketIndex;
        var name = propertyName[..nameLength];
        var suffix = bracketIndex < 0 ? string.Empty : propertyName[bracketIndex..];

        return name.Length == 0
            ? "$"
            : char.ToLowerInvariant(name[0]) + name[1..] + suffix;
    }
}
