using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record DemoLaunchOptions(
    string? CentralEnvironment = null,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    IReadOnlyDictionary<string, string>? CommandLineOverrides = null);

internal static class DemoLaunchOptionsValidator
{
    private const string CentralRequiredKeys = "CentralConfig:CentralRequiredKeys";
    private const string FallbackAllowedKeys = "CentralConfig:FallbackAllowedKeys";
    private static readonly string[] HarnessOwnedSettings =
    [
        "urls",
        "contentRoot",
        "CentralConfig:BaseAddress",
        "CentralConfig:Enabled"
    ];
    private static readonly string[] HarnessOwnedHostingEnvironmentAliases =
    [
        "ASPNETCORE_URLS",
        "ASPNETCORE_CONTENTROOT",
        "DOTNET_URLS",
        "DOTNET_CONTENTROOT"
    ];

    internal static void Validate(DemoLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.CentralEnvironment is not null
            && string.IsNullOrWhiteSpace(options.CentralEnvironment))
        {
            throw new ArgumentException(
                "The Demo Central environment override must not be empty.",
                nameof(options));
        }

        ValidateOverrides(options.EnvironmentOverrides, options, isEnvironmentOverride: true);
        ValidateOverrides(options.CommandLineOverrides, options, isEnvironmentOverride: false);
    }

    private static void ValidateOverrides(
        IReadOnlyDictionary<string, string>? overrides,
        DemoLaunchOptions options,
        bool isEnvironmentOverride)
    {
        if (overrides is null)
        {
            return;
        }

        foreach ((string name, string value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.StartsWith("-", StringComparison.Ordinal)
                || name.Contains('=')
                || value is null)
            {
                throw new ArgumentException(
                    "Demo override names and values must be valid.",
                    nameof(options));
            }

            if (AddressesRestrictedConfigurationKey(name))
            {
                throw new InvalidOperationException(
                    "Scenario launch inputs must not override required-key or fallback-key selection.");
            }

            if (AddressesHarnessOwnedSetting(name)
                || (isEnvironmentOverride && AddressesHarnessOwnedHostingEnvironmentAlias(name)))
            {
                throw new InvalidOperationException(
                    "Scenario launch inputs must not override harness-owned settings.");
            }
        }
    }

    private static bool AddressesRestrictedConfigurationKey(string candidate)
    {
        string normalized = candidate.Replace("__", ":", StringComparison.Ordinal);
        return IsRestrictedSection(normalized, CentralRequiredKeys)
            || IsRestrictedSection(normalized, FallbackAllowedKeys);
    }

    private static bool IsRestrictedSection(string candidate, string section) =>
        string.Equals(candidate, section, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase);

    private static bool AddressesHarnessOwnedSetting(string candidate)
    {
        string normalized = candidate.Replace("__", ":", StringComparison.Ordinal);
        return HarnessOwnedSettings.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool AddressesHarnessOwnedHostingEnvironmentAlias(string candidate) =>
        HarnessOwnedHostingEnvironmentAliases.Contains(candidate, StringComparer.OrdinalIgnoreCase);
}

internal sealed record EffectiveConfigurationResponse(
    string Scope,
    IReadOnlyDictionary<string, string> EffectiveValues)
{
    internal string GetValue(string key) =>
        EffectiveValues.Single(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}

internal sealed record DemoHttpResult<TBody>(
    HttpStatusCode StatusCode,
    string? MediaType,
    bool HasNoStore,
    TBody Body);

internal sealed record UnsafeRootConfigurationResponse(
    string Scope,
    string Warning,
    IReadOnlyDictionary<string, string> EffectiveValues)
{
    internal string GetValue(string key) =>
        EffectiveValues.Single(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}

internal static class DemoHttpResponseReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<DemoHttpResult<TBody>> ReadAsync<TBody>(
        HttpResponseMessage response,
        string route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"GET {route} returned status {(int)response.StatusCode}.");
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"GET {route} returned status {(int)response.StatusCode} with an invalid media type.");
        }

        TBody? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<TBody>(
                JsonOptions,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || exception is IOException)
        {
            throw new InvalidOperationException($"GET {route} failed.");
        }
        catch (Exception exception) when (
            exception is JsonException
            || exception is NotSupportedException)
        {
            throw new InvalidOperationException(
                $"GET {route} returned status {(int)response.StatusCode} with an invalid response contract.");
        }

        if (body is null)
        {
            throw new InvalidOperationException(
                $"GET {route} returned status {(int)response.StatusCode} without a response contract.");
        }

        return new DemoHttpResult<TBody>(
            response.StatusCode,
            mediaType,
            response.Headers.CacheControl?.NoStore == true,
            body);
    }
}
