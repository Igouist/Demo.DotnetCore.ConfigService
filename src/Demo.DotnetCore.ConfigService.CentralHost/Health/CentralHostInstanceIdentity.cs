using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class CentralHostInstanceIdentity
{
    public const string ConfigurationKey = "CentralHealth:InstanceId";
    private const string InvalidMessage =
        "Central health instance identity configuration is invalid.";

    private CentralHostInstanceIdentity(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static CentralHostInstanceIdentity FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configuredValue = configuration[ConfigurationKey];
        if (configuredValue is null)
        {
            return new CentralHostInstanceIdentity(Guid.NewGuid().ToString("N"));
        }

        if (!Guid.TryParseExact(configuredValue, "N", out Guid parsed)
            || !StringComparer.Ordinal.Equals(parsed.ToString("N"), configuredValue))
        {
            throw new InvalidOperationException(InvalidMessage);
        }

        return new CentralHostInstanceIdentity(configuredValue);
    }
}
