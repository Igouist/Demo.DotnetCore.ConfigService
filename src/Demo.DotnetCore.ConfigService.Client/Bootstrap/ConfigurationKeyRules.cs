namespace Demo.DotnetCore.ConfigService.Client;

internal static class ConfigurationKeyRules
{
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = value.Split(':');
        return segments.All(segment => !string.IsNullOrWhiteSpace(segment))
            && !string.Equals(segments[0], "CentralConfig", StringComparison.OrdinalIgnoreCase);
    }
}
