using Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Packaging;

[Collection(EndToEndCollection.Name)]
public sealed class PackagedDemoArtifactTests(PackagedDemoFixture fixture)
{
    [Fact]
    public void Fixture_launch_target_is_the_freshly_packed_client_dependency()
    {
        PackagedDemoEvidence evidence = fixture.Evidence;

        Assert.True(
            evidence.PackageVersion.StartsWith("0.1.0-e2e.", StringComparison.Ordinal),
            evidence.PackageVersion);
        Assert.True(File.Exists(evidence.PackagePath), evidence.PackagePath);
        Assert.True(File.Exists(evidence.DemoAssemblyPath), evidence.DemoAssemblyPath);
        Assert.True(File.Exists(evidence.AssetsPath), evidence.AssetsPath);
        Assert.True(File.Exists(evidence.DependencyManifestPath), evidence.DependencyManifestPath);
        Assert.Equal(evidence.BuiltClientSha256, evidence.PackagedClientSha256);
        Assert.Equal(evidence.BuiltClientSha256, evidence.ResolvedClientSha256);
        Assert.True(
            Path.GetFullPath(evidence.ResolvedClientAssemblyPath).StartsWith(
                Path.GetFullPath(fixture.PrimaryPackagePath) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            evidence.ResolvedClientAssemblyPath);
    }
}
