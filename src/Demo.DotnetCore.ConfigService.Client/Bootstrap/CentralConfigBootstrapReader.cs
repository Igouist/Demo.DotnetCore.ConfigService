using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.Client;

internal static class CentralConfigBootstrapReader
{
    private const string Prefix = "CentralConfig:";
    private const string ResolveRoute = "api/v1/distribution/configurations/resolve";

    internal static BootstrapReadResult Read(IConfiguration configuration)
    {
        string? rawEnabled = configuration[$"{Prefix}Enabled"];
        if (!bool.TryParse(rawEnabled, out bool enabled))
        {
            return BootstrapReadResult.Invalid;
        }

        if (!enabled)
        {
            return BootstrapReadResult.Disabled;
        }

        string? environment = configuration[$"{Prefix}Environment"];
        if (string.IsNullOrWhiteSpace(environment)
            || !string.Equals(environment, environment.Trim(), StringComparison.Ordinal)
            || !TryResolveEndpoint(configuration[$"{Prefix}BaseAddress"], out Uri? endpoint)
            || !TryReadSelection(configuration, out KeySelection? selection))
        {
            return BootstrapReadResult.Invalid;
        }

        return BootstrapReadResult.Enabled(
            new EnabledCentralConfigBootstrap(environment, endpoint, selection));
    }

    private static bool TryReadSelection(
        IConfiguration configuration,
        [NotNullWhen(true)] out KeySelection? selection)
    {
        if (!TryReadArray(
                configuration.GetSection($"{Prefix}CentralRequiredKeys"),
                out string?[] required)
            || !TryReadArray(
                configuration.GetSection($"{Prefix}FallbackAllowedKeys"),
                out string?[] fallback))
        {
            selection = null;
            return false;
        }

        return KeySelection.TryCreate(required, fallback, out selection);
    }

    private static bool TryReadArray(
        IConfigurationSection section,
        out string?[] values)
    {
        values = [];
        if (section.Value is not null)
        {
            return false;
        }

        IConfigurationSection[] children = section.GetChildren().ToArray();
        for (int index = 0; index < children.Length; index++)
        {
            string expectedKey = index.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(children[index].Key, expectedKey, StringComparison.Ordinal))
            {
                return false;
            }
        }

        values = children.Select(child => child.Value).ToArray();
        return true;
    }

    private static bool TryResolveEndpoint(
        string? rawBaseAddress,
        [NotNullWhen(true)] out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(rawBaseAddress)
            || !Uri.TryCreate(rawBaseAddress, UriKind.Absolute, out Uri? baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(baseAddress.Host)
            || !string.IsNullOrEmpty(baseAddress.Query)
            || !string.IsNullOrEmpty(baseAddress.Fragment)
            || !string.IsNullOrEmpty(baseAddress.UserInfo))
        {
            return false;
        }

        string normalized = baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        endpoint = new Uri(new Uri(normalized, UriKind.Absolute), ResolveRoute);
        return true;
    }
}
