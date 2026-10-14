using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class CentralHostReadinessEndpoint
{
    public const string Route = "/health/ready";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static IEndpointConventionBuilder MapCentralHostReadinessProbe(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(Route, WriteResponseAsync);
    }

    private static async Task WriteResponseAsync(
        HttpContext context,
        HealthCheckService healthChecks,
        CentralHostInstanceIdentity identity)
    {
        HealthReport report = await healthChecks.CheckHealthAsync(
            static registration =>
                registration.Tags.Contains(SqliteReadinessHealthCheck.ReadinessTag),
            context.RequestAborted);
        bool ready = report.Status == HealthStatus.Healthy;

        context.Response.StatusCode = ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new CentralHostReadinessResponse(
                ready ? "ready" : "unavailable",
                identity.Value),
            JsonOptions,
            context.RequestAborted);
    }

    private sealed record CentralHostReadinessResponse(
        string Status,
        string InstanceId);
}
