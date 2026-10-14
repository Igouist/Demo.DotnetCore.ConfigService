using System.Text.Json;
using Demo.DotnetCore.ConfigService.DemoHost.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.DemoHost.Tests.Conventions;

public sealed class DemoAppSettingsConventionTests
{
    [Fact]
    public void Base_file_owns_explicit_activation_and_both_key_selection_arrays()
    {
        string demoDirectory = GetDemoDirectory();
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(demoDirectory, "appsettings.json")));
        JsonElement root = document.RootElement;
        JsonElement central = root.GetProperty("CentralConfig");

        Assert.Equal(JsonValueKind.False, central.GetProperty("Enabled").ValueKind);
        Assert.Equal(
            ["Demo:CentralRequiredMessage"],
            ReadStrings(central.GetProperty("CentralRequiredKeys")));
        Assert.Equal(
            ["Demo:FallbackAllowedMessage"],
            ReadStrings(central.GetProperty("FallbackAllowedKeys")));
        Assert.Equal(
            "local-fallback",
            root.GetProperty("Demo").GetProperty("FallbackAllowedMessage").GetString());
        Assert.Equal(
            "local-only",
            root.GetProperty("Demo").GetProperty("LocalOnlyMessage").GetString());
    }

    [Fact]
    public void Environment_files_do_not_declare_key_selection_arrays()
    {
        foreach (string path in Directory.GetFiles(GetDemoDirectory(), "appsettings.*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("CentralConfig", out JsonElement central))
            {
                continue;
            }

            Assert.False(central.TryGetProperty("CentralRequiredKeys", out _), path);
            Assert.False(central.TryGetProperty("FallbackAllowedKeys", out _), path);
        }
    }

    private static string GetDemoDirectory() =>
        Path.Combine(
            RepositoryPaths.FindRoot(),
            "src",
            "Demo.DotnetCore.ConfigService.DemoHost");

    private static string[] ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(static item => item.GetString()!).ToArray();
}
