using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Health;

public sealed class CentralHostInstanceIdentityTests
{
    private const string CanonicalIdentity = "794c1ca58c1342a6863d81e152777b17";

    [Fact]
    public void FromConfiguration_preserves_a_canonical_configured_identity()
    {
        CentralHostInstanceIdentity identity = CentralHostInstanceIdentity.FromConfiguration(
            BuildConfiguration(CanonicalIdentity));

        Assert.Equal(CanonicalIdentity, identity.Value);
    }

    [Fact]
    public void FromConfiguration_generates_a_fresh_canonical_identity_when_the_setting_is_absent()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        CentralHostInstanceIdentity identity =
            CentralHostInstanceIdentity.FromConfiguration(configuration);
        CentralHostInstanceIdentity nextIdentity =
            CentralHostInstanceIdentity.FromConfiguration(configuration);

        Assert.True(Guid.TryParseExact(identity.Value, "N", out Guid parsed));
        Assert.Equal(parsed.ToString("N"), identity.Value);
        Assert.NotEqual(identity.Value, nextIdentity.Value);
    }

    [Fact]
    public void AddCentralConfigHost_registers_one_stable_generated_identity()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CentralConfigDatabase"] = "Data Source=central.db"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddCentralConfigHost(configuration, Path.GetTempPath());
        using ServiceProvider provider = services.BuildServiceProvider();

        CentralHostInstanceIdentity first =
            provider.GetRequiredService<CentralHostInstanceIdentity>();
        CentralHostInstanceIdentity second =
            provider.GetRequiredService<CentralHostInstanceIdentity>();

        Assert.Same(first, second);
        Assert.True(Guid.TryParseExact(first.Value, "N", out Guid parsed));
        Assert.Equal(parsed.ToString("N"), first.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("794C1CA58C1342A6863D81E152777B17")]
    [InlineData("794c1ca5-8c13-42a6-863d-81e152777b17")]
    [InlineData("not-an-instance-id")]
    public void FromConfiguration_rejects_present_noncanonical_identities_with_a_safe_fixed_message(
        string configuredValue)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => CentralHostInstanceIdentity.FromConfiguration(
                BuildConfiguration(configuredValue)));

        Assert.Equal("Central health instance identity configuration is invalid.", exception.Message);
        if (configuredValue.Length > 0)
        {
            Assert.DoesNotContain(configuredValue, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AddCentralConfigHost_rejects_an_invalid_identity_during_service_registration()
    {
        const string configuredValue = "NOT-A-CANONICAL-IDENTITY";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CentralConfigDatabase"] = "Data Source=central.db",
                ["CentralHealth:InstanceId"] = configuredValue
            })
            .Build();
        var services = new ServiceCollection();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddCentralConfigHost(configuration, Path.GetTempPath()));

        Assert.Equal("Central health instance identity configuration is invalid.", exception.Message);
        Assert.DoesNotContain(configuredValue, exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(string configuredValue) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CentralHealth:InstanceId"] = configuredValue
            })
            .Build();
}
