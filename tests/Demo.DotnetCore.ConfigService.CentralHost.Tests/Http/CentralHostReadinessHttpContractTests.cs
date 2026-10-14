using System.Net;
using System.Text.Json;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class CentralHostReadinessHttpContractTests
{
    [Fact]
    public async Task Host_registers_the_ready_sqlite_check_with_the_shared_timeout()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        HealthCheckRegistration registration = Assert.Single(
            options.Registrations,
            candidate => candidate.Name == SqliteReadinessHealthCheck.Name);
        Assert.Equal(HealthStatus.Unhealthy, registration.FailureStatus);
        Assert.Equal(
            TimeSpan.FromSeconds(CentralConfigDatabaseOptions.OperationTimeoutSeconds),
            registration.Timeout);
        Assert.Equal(
            [SqliteReadinessHealthCheck.ReadinessTag],
            registration.Tags.OrderBy(static tag => tag, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Readiness_returns_the_exact_ready_contract_for_the_configured_instance()
    {
        await using var factory = new CentralHostWebApplicationFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertReadinessResponseAsync(
            response,
            HttpStatusCode.OK,
            "ready",
            CentralHostWebApplicationFactory.InstanceId);
    }

    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task Readiness_maps_nonhealthy_reports_to_the_exact_unavailable_contract(
        HealthStatus forcedStatus)
    {
        await using var baseFactory = new CentralHostWebApplicationFactory();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "forced-ready-status",
                    () => forcedStatus == HealthStatus.Degraded
                        ? HealthCheckResult.Degraded(
                            "database-path-secret",
                            data: new Dictionary<string, object> { ["secret"] = "value" })
                        : HealthCheckResult.Unhealthy(
                            "connection-string-secret",
                            new InvalidOperationException("sqlite-secret")),
                    tags: [SqliteReadinessHealthCheck.ReadinessTag])));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertReadinessResponseAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "unavailable",
            CentralHostWebApplicationFactory.InstanceId);
    }

    [Fact]
    public async Task Readiness_selects_only_ready_tagged_checks()
    {
        await using var baseFactory = new CentralHostWebApplicationFactory();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "untagged-failure",
                    () => HealthCheckResult.Unhealthy("must-not-run"))));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");

        await AssertReadinessResponseAsync(
            response,
            HttpStatusCode.OK,
            "ready",
            CentralHostWebApplicationFactory.InstanceId);
    }

    private static async Task AssertReadinessResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode,
        string expectedStatus,
        string expectedInstanceId)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            ["no-store"],
            response.Headers.GetValues("Cache-Control"));

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            ["instanceId", "status"],
            root.EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(expectedInstanceId, root.GetProperty("instanceId").GetString());
    }
}
