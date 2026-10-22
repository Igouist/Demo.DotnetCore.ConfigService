using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Demo.DotnetCore.ConfigService.CentralHost;

// POC-064: one shared demo key protects the whole management surface. It is a stand-in, not real
// authentication: a sign-in or AD integration replaces this handler while the policy stays the same.
internal static class ManagementApiKeyDefaults
{
    public const string Scheme = "ManagementApiKey";
    public const string Policy = "ManagementApi";
    public const string HeaderName = "X-Api-Key";
    public const string ConfigurationKey = "ManagementApi:ApiKey";
}

internal sealed class ManagementApiKeyOptions : AuthenticationSchemeOptions
{
    public string? ApiKey { get; set; }
}

internal sealed class ManagementApiKeyHandler : AuthenticationHandler<ManagementApiKeyOptions>
{
    private readonly CentralProblemDetailsWriter _problemDetailsWriter;

    public ManagementApiKeyHandler(
        IOptionsMonitor<ManagementApiKeyOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        CentralProblemDetailsWriter problemDetailsWriter)
        : base(options, logger, encoder) =>
        _problemDetailsWriter = problemDetailsWriter;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = Options.ApiKey;
        if (string.IsNullOrEmpty(configuredKey))
        {
            // An unset key keeps the management surface closed instead of silently turning protection off.
            return Task.FromResult(AuthenticateResult.Fail("The management API key is not configured."));
        }

        if (!Request.Headers.TryGetValue(ManagementApiKeyDefaults.HeaderName, out var presented)
            || presented.Count != 1
            || !FixedTimeEquals(presented[0], configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("A valid management API key is required."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "management-api-key")],
            ManagementApiKeyDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), ManagementApiKeyDefaults.Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        _problemDetailsWriter.WriteAsync(
            Context,
            new CentralProblemDescriptor(
                StatusCodes.Status401Unauthorized,
                "unauthorized",
                "A valid management API key is required."),
            Context.RequestAborted);

    // Hashing first gives both inputs the same length, so the comparison time does not reveal the key length.
    private static bool FixedTimeEquals(string? presented, string configured) =>
        presented is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
}
