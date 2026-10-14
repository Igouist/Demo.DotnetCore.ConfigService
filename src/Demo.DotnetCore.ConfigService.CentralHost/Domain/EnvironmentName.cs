namespace Demo.DotnetCore.ConfigService.CentralHost;

internal readonly struct EnvironmentName : IEquatable<EnvironmentName>
{
    public string Value { get; }

    private EnvironmentName(string value) => Value = value;

    public static bool TryCreate(string? candidate, out EnvironmentName value)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate != candidate.Trim())
        {
            value = default;
            return false;
        }

        value = new EnvironmentName(candidate);
        return true;
    }

    public static EnvironmentName FromValidated(string value) =>
        TryCreate(value, out var environment)
            ? environment
            : throw new ArgumentException("The environment name must be validated before construction.", nameof(value));

    public bool Equals(EnvironmentName other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is EnvironmentName other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value;
}
