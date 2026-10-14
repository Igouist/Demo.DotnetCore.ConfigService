namespace Demo.DotnetCore.ConfigService.CentralHost;

internal readonly struct ConfigurationKey : IEquatable<ConfigurationKey>
{
    public string Value { get; }

    private ConfigurationKey(string value) => Value = value;

    public static bool TryCreate(string? candidate, out ConfigurationKey value)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate != candidate.Trim())
        {
            value = default;
            return false;
        }

        var segments = candidate.Split(':');
        if (segments.Any(string.IsNullOrWhiteSpace) || StringComparer.OrdinalIgnoreCase.Equals(segments[0], "CentralConfig"))
        {
            value = default;
            return false;
        }

        value = new ConfigurationKey(candidate);
        return true;
    }

    public static ConfigurationKey FromValidated(string value) =>
        TryCreate(value, out var key)
            ? key
            : throw new ArgumentException("The configuration key must be validated before construction.", nameof(value));

    public bool Equals(ConfigurationKey other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ConfigurationKey other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value;
}
