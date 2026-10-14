namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class PackagedDemoFixture : IAsyncLifetime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private TemporaryDirectory? temporaryDirectory;

    internal string RepositoryRoot { get; private set; } = string.Empty;

    internal string DotnetHostPath { get; private set; } = string.Empty;

    internal string CentralAssemblyPath { get; private set; } = string.Empty;

    internal string DemoAssemblyPath => Evidence.DemoAssemblyPath;

    internal string PrimaryPackagePath { get; private set; } = string.Empty;

    internal PackagedDemoEvidence Evidence { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        TemporaryDirectory fixtureDirectory = TemporaryDirectory.Create("config-service-e2e-fixture-");

        try
        {
            RepositoryRoot = LocateRepositoryRoot();
            DotnetHostPath = Path.GetFullPath(DotnetHostLocator.Resolve());

            string fixtureRoot = fixtureDirectory.Path;
            string packageFeedPath = Path.Combine(fixtureRoot, "feed");
            PrimaryPackagePath = Path.Combine(fixtureRoot, "packages");
            string httpCachePath = Path.Combine(fixtureRoot, "nuget-http-cache");
            string pluginsCachePath = Path.Combine(fixtureRoot, "nuget-plugins-cache");
            string clientIntermediatePath = Path.Combine(fixtureRoot, "client-obj");
            string clientOutputPath = Path.Combine(fixtureRoot, "client-bin");
            string demoIntermediatePath = Path.Combine(fixtureRoot, "demo-obj");
            string demoOutputPath = Path.Combine(fixtureRoot, "demo-bin");
            string nugetConfigurationPath = Path.Combine(fixtureRoot, "NuGet.Config");

            foreach (string directory in new[]
            {
                packageFeedPath,
                PrimaryPackagePath,
                httpCachePath,
                pluginsCachePath,
                Path.Combine(fixtureRoot, "cli-home"),
                Path.Combine(fixtureRoot, "temp"),
                clientIntermediatePath,
                clientOutputPath,
                demoIntermediatePath,
                demoOutputPath
            })
            {
                Directory.CreateDirectory(directory);
            }

            string fallbackPackagePath = ResolveFallbackPackagePath();
            string packageVersion = CreateUniquePackageVersion(fallbackPackagePath);
            NuGetConfiguration.Write(
                nugetConfigurationPath,
                packageFeedPath,
                fallbackPackagePath);

            IReadOnlyDictionary<string, string> buildEnvironment = ProcessEnvironment.CreateBuild(
                fixtureRoot,
                PrimaryPackagePath,
                httpCachePath,
                pluginsCachePath);
            string clientProjectPath = Path.GetFullPath(Path.Combine(
                RepositoryRoot,
                "src",
                "Demo.DotnetCore.ConfigService.Client",
                "Demo.DotnetCore.ConfigService.Client.csproj"));
            string demoProjectPath = Path.GetFullPath(Path.Combine(
                RepositoryRoot,
                "src",
                "Demo.DotnetCore.ConfigService.DemoHost",
                "Demo.DotnetCore.ConfigService.DemoHost.csproj"));
            string clientIntermediateArgument = EnsureTrailingDirectorySeparator(clientIntermediatePath);
            string clientOutputArgument = EnsureTrailingDirectorySeparator(clientOutputPath);
            string demoIntermediateArgument = EnsureTrailingDirectorySeparator(demoIntermediatePath);
            string demoOutputArgument = EnsureTrailingDirectorySeparator(demoOutputPath);

            await RunDotnetAsync(
                buildEnvironment,
                "restore",
                clientProjectPath,
                "--configfile",
                nugetConfigurationPath,
                "--nologo",
                "-maxcpucount:1",
                "-nodeReuse:false",
                $"-p:RestorePackagesPath={PrimaryPackagePath}",
                $"-p:BaseIntermediateOutputPath={clientIntermediateArgument}",
                $"-p:BaseOutputPath={clientOutputArgument}",
                "-p:DefaultItemExcludesInProjectFolder=obj/**");

            await RunDotnetAsync(
                buildEnvironment,
                "build",
                clientProjectPath,
                "-c",
                "Release",
                "--no-restore",
                "--nologo",
                "-maxcpucount:1",
                "-nodeReuse:false",
                "-p:UseSharedCompilation=false",
                $"-p:RestorePackagesPath={PrimaryPackagePath}",
                $"-p:BaseIntermediateOutputPath={clientIntermediateArgument}",
                $"-p:BaseOutputPath={clientOutputArgument}",
                "-p:DefaultItemExcludesInProjectFolder=obj/**",
                "-p:EnforceCodeStyleInBuild=true");

            await RunDotnetAsync(
                buildEnvironment,
                "pack",
                clientProjectPath,
                "-c",
                "Release",
                "--no-build",
                "--no-restore",
                "--nologo",
                "-maxcpucount:1",
                "-nodeReuse:false",
                "--output",
                packageFeedPath,
                $"-p:PackageVersion={packageVersion}",
                $"-p:RestorePackagesPath={PrimaryPackagePath}",
                $"-p:BaseIntermediateOutputPath={clientIntermediateArgument}",
                $"-p:BaseOutputPath={clientOutputArgument}",
                "-p:DefaultItemExcludesInProjectFolder=obj/**");

            await RunDotnetAsync(
                buildEnvironment,
                "restore",
                demoProjectPath,
                "--configfile",
                nugetConfigurationPath,
                "--nologo",
                "-maxcpucount:1",
                "-nodeReuse:false",
                "-p:UsePackagedClient=true",
                $"-p:DemoClientPackageVersion={packageVersion}",
                $"-p:RestorePackagesPath={PrimaryPackagePath}",
                $"-p:BaseIntermediateOutputPath={demoIntermediateArgument}",
                $"-p:BaseOutputPath={demoOutputArgument}",
                "-p:DefaultItemExcludesInProjectFolder=obj/**");

            await RunDotnetAsync(
                buildEnvironment,
                "build",
                demoProjectPath,
                "-c",
                "Release",
                "--no-restore",
                "--nologo",
                "-maxcpucount:1",
                "-nodeReuse:false",
                "-p:UseSharedCompilation=false",
                "-p:UsePackagedClient=true",
                $"-p:DemoClientPackageVersion={packageVersion}",
                $"-p:RestorePackagesPath={PrimaryPackagePath}",
                $"-p:BaseIntermediateOutputPath={demoIntermediateArgument}",
                $"-p:BaseOutputPath={demoOutputArgument}",
                "-p:DefaultItemExcludesInProjectFolder=obj/**",
                "-p:EnforceCodeStyleInBuild=true");

            string builtClientAssemblyPath = Path.Combine(
                clientOutputPath,
                "Release",
                "net8.0",
                "Demo.DotnetCore.ConfigService.Client.dll");
            string packagePath = Path.Combine(
                packageFeedPath,
                $"Demo.DotnetCore.ConfigService.Client.{packageVersion}.nupkg");
            string demoAssemblyPath = Path.Combine(
                demoOutputPath,
                "Release",
                "net8.0",
                "Demo.DotnetCore.ConfigService.DemoHost.dll");
            string assetsPath = Path.Combine(demoIntermediatePath, "project.assets.json");
            string dependencyManifestPath = Path.Combine(
                demoOutputPath,
                "Release",
                "net8.0",
                "Demo.DotnetCore.ConfigService.DemoHost.deps.json");

            Evidence = PackageArtifactVerifier.Verify(
                packageVersion,
                packagePath,
                builtClientAssemblyPath,
                PrimaryPackagePath,
                demoAssemblyPath,
                assetsPath,
                dependencyManifestPath);

            CentralAssemblyPath = Path.GetFullPath(Path.Combine(
                RepositoryRoot,
                "src",
                "Demo.DotnetCore.ConfigService.CentralHost",
                "bin",
                "Release",
                "net8.0",
                "Demo.DotnetCore.ConfigService.CentralHost.dll"));
            if (!File.Exists(CentralAssemblyPath))
            {
                throw new FileNotFoundException(
                    "The build-only CentralHost project reference did not produce the required Release assembly.",
                    CentralAssemblyPath);
            }

            temporaryDirectory = fixtureDirectory;
        }
        catch (Exception initializationException)
        {
            try
            {
                await fixtureDirectory.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(initializationException, cleanupException);
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        TemporaryDirectory? ownedDirectory = Interlocked.Exchange(ref temporaryDirectory, null);
        if (ownedDirectory is not null)
        {
            await ownedDirectory.DisposeAsync();
        }
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Demo.DotnetCore.ConfigService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Unable to locate Demo.DotnetCore.ConfigService.sln above '{AppContext.BaseDirectory}'.");
    }

    private static string ResolveFallbackPackagePath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(userProfile, ".nuget", "packages"));
    }

    private static string CreateUniquePackageVersion(string fallbackPackagePath)
    {
        string version;
        do
        {
            version = $"0.1.0-e2e.{Guid.NewGuid():N}";
        }
        while (Directory.Exists(Path.Combine(
            fallbackPackagePath,
            "demo.dotnetcore.configservice.client",
            version.ToLowerInvariant())));

        return version;
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private Task<DotnetCommandResult> RunDotnetAsync(
        IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        return DotnetCommandRunner.RunAsync(
            DotnetHostPath,
            new DotnetCommandOptions(
                RepositoryRoot,
                arguments,
                environment,
                CommandTimeout));
    }
}
