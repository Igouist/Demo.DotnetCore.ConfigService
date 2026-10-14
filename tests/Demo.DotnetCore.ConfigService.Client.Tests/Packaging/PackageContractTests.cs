using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Packaging;

public sealed class PackageContractTests
{
    private const string PackageId = "Demo.DotnetCore.ConfigService.Client";
    private const string PackageVersion = "0.1.0";
    private const string PackageAuthors = "Demo.DotnetCore.ConfigService";
    private const string PackageDescription =
        "Startup-only central configuration client for the Config Distribution .NET 8 POC.";
    private const string RepositoryType = "git";
    private const string RepositoryUrl =
        "https://github.com/Igouist/Demo.DotnetCore.ConfigService";
    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public void Assembly_exports_only_the_accepted_client_types()
    {
        Type[] exported = typeof(CentralConfigLoadException).Assembly.ExportedTypes
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new Type[]
            {
                typeof(CentralConfigFailureKind),
                typeof(CentralConfigHostApplicationBuilderExtensions),
                typeof(CentralConfigLoadException)
            }.OrderBy(type => type.FullName, StringComparer.Ordinal),
            exported);
    }

    [Fact]
    public void Exactly_two_async_extension_overloads_are_public()
    {
        MethodInfo[] methods = typeof(CentralConfigHostApplicationBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.Equal(2, methods.Length);
        Assert.All(methods, method =>
        {
            Assert.Equal("AddCentralConfigAsync", method.Name);
            Assert.Equal(typeof(Task), method.ReturnType);
            Assert.True(method.IsDefined(typeof(ExtensionAttribute), inherit: false));
            Assert.Equal(typeof(IHostApplicationBuilder), method.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
            Assert.True(method.GetParameters()[^1].HasDefaultValue);
        });
        Assert.Contains(methods, method => method.GetParameters().Length == 2);
        Assert.Contains(methods, method =>
            method.GetParameters().Length == 3
            && method.GetParameters()[1].ParameterType == typeof(ILogger));
    }

    [Fact]
    public async Task Packed_archive_matches_the_accepted_contract()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            PackageId,
            $"{PackageId}.csproj");
        string sourceReadmePath = Path.Combine(repositoryRoot, "src", PackageId, "README.md");
        string currentConfiguration = typeof(PackageContractTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidOperationException("The current test assembly has no build configuration.");
        string currentAssemblyPath = typeof(CentralConfigLoadException).Assembly.Location;
        Assert.True(File.Exists(currentAssemblyPath), $"Loaded Client assembly not found: {currentAssemblyPath}");

        string packageOutputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"{PackageId}.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageOutputDirectory);

        try
        {
            await RunPackAsync(
                repositoryRoot,
                projectPath,
                packageOutputDirectory,
                currentConfiguration);

            string packagePath = Assert.Single(
                Directory.GetFiles(packageOutputDirectory, "*.nupkg", SearchOption.TopDirectoryOnly));
            Assert.Equal($"{PackageId}.{PackageVersion}.nupkg", Path.GetFileName(packagePath));

            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            string[] fixedEntries =
            [
                "_rels/.rels",
                $"{PackageId}.nuspec",
                $"lib/net8.0/{PackageId}.dll",
                "README.md",
                "[Content_Types].xml"
            ];
            string[] entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Equal(6, entryNames.Length);
            Assert.All(fixedEntries, expected =>
                Assert.Single(entryNames, actual => string.Equals(actual, expected, StringComparison.Ordinal)));
            Assert.Single(entryNames, IsCorePropertiesEntry);
            Assert.DoesNotContain(
                entryNames,
                entry => !fixedEntries.Contains(entry, StringComparer.Ordinal)
                    && !IsCorePropertiesEntry(entry));

            byte[] sourceReadmeBytes = File.ReadAllBytes(sourceReadmePath);
            byte[] packagedReadmeBytes = ReadEntryBytes(archive, "README.md");
            Assert.Equal(sourceReadmeBytes, packagedReadmeBytes);
            Assert.Equal(
                File.ReadAllText(sourceReadmePath, Encoding.UTF8),
                ReadUtf8Text(packagedReadmeBytes));

            byte[] currentAssemblyBytes = File.ReadAllBytes(currentAssemblyPath);
            byte[] packagedAssemblyBytes = ReadEntryBytes(
                archive,
                $"lib/net8.0/{PackageId}.dll");
            Assert.Equal(currentAssemblyBytes, packagedAssemblyBytes);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(currentAssemblyBytes)),
                Convert.ToHexString(SHA256.HashData(packagedAssemblyBytes)));

            AssertNuspecContract(ReadEntryBytes(archive, $"{PackageId}.nuspec"));
        }
        finally
        {
            Directory.Delete(packageOutputDirectory, recursive: true);
        }
    }

    private static void AssertNuspecContract(byte[] nuspecBytes)
    {
        XDocument nuspec = XDocument.Parse(ReadUtf8Text(nuspecBytes), LoadOptions.None);
        XElement metadata = Assert.Single(
            nuspec.Descendants(),
            element => element.Name.LocalName == "metadata");
        XElement version = Assert.Single(
            metadata.Elements(),
            element => element.Name.LocalName == "version");
        Assert.Equal(PackageVersion, version.Value);
        AssertMetadataValue(metadata, "id", PackageId);
        AssertMetadataValue(metadata, "authors", PackageAuthors);
        AssertMetadataValue(metadata, "description", PackageDescription);
        AssertMetadataValue(metadata, "readme", "README.md");
        XElement? licenseAcceptance = metadata.Elements()
            .SingleOrDefault(element => element.Name.LocalName == "requireLicenseAcceptance");
        bool requiresLicenseAcceptance = licenseAcceptance is not null
            && System.Xml.XmlConvert.ToBoolean(licenseAcceptance.Value);
        Assert.False(requiresLicenseAcceptance);
        XElement repository = Assert.Single(
            metadata.Elements(),
            element => element.Name.LocalName == "repository");
        Assert.Equal(RepositoryType, repository.Attribute("type")?.Value);
        Assert.Equal(RepositoryUrl, repository.Attribute("url")?.Value);

        (string Id, string Version)[] dependencies = metadata
            .Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element =>
            {
                XAttribute id = element.Attribute("id")
                    ?? throw new InvalidDataException("A packaged dependency is missing its id.");
                XAttribute dependencyVersion = element.Attribute("version")
                    ?? throw new InvalidDataException($"Packaged dependency {id.Value} is missing its version.");
                return (Id: id.Value, Version: dependencyVersion.Value);
            })
            .OrderBy(dependency => dependency.Id, StringComparer.Ordinal)
            .ToArray();
        (string Id, string Version)[] expectedDependencies =
        [
            ("Microsoft.Extensions.Configuration", "8.0.0"),
            ("Microsoft.Extensions.Configuration.Json", "8.0.0"),
            ("Microsoft.Extensions.Hosting.Abstractions", "8.0.0"),
            ("Microsoft.Extensions.Logging.Abstractions", "8.0.0"),
            ("System.Text.Json", "8.0.6")
        ];

        Assert.Equal(expectedDependencies, dependencies);
    }

    private static void AssertMetadataValue(
        XElement metadata,
        string elementName,
        string expectedValue)
    {
        XElement element = Assert.Single(
            metadata.Elements(),
            candidate => candidate.Name.LocalName == elementName);
        Assert.Equal(expectedValue, element.Value);
    }

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => string.Equals(candidate.FullName, entryName, StringComparison.Ordinal));
        using Stream source = entry.Open();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static string ReadUtf8Text(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool IsCorePropertiesEntry(string entryName)
    {
        const string prefix = "package/services/metadata/core-properties/";
        const string suffix = ".psmdcp";
        if (!entryName.StartsWith(prefix, StringComparison.Ordinal)
            || !entryName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string fileNameWithoutExtension = entryName[prefix.Length..^suffix.Length];
        return fileNameWithoutExtension.Length > 0
            && !fileNameWithoutExtension.Contains('/', StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Demo.DotnetCore.ConfigService.sln"))
                && File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    PackageId,
                    $"{PackageId}.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }

    private static async Task RunPackAsync(
        string repositoryRoot,
        string projectPath,
        string packageOutputDirectory,
        string configuration)
    {
        string dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHostPath,
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        string[] arguments =
        [
            "pack",
            projectPath,
            "--configuration",
            configuration,
            "--no-build",
            "--no-restore",
            "--output",
            packageOutputDirectory,
            "--nologo",
            "-maxcpucount:1",
            "-nodeReuse:false"
        ];
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start dotnet host {dotnetHostPath}.");
        }

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(PackTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            string timedOutOutput = await standardOutputTask;
            string timedOutError = await standardErrorTask;
            throw new TimeoutException(
                $"dotnet pack exceeded {PackTimeout}.\nStandard output:\n{timedOutOutput}\nStandard error:\n{timedOutError}");
        }

        string standardOutput = await standardOutputTask;
        string standardError = await standardErrorTask;
        Assert.True(
            process.ExitCode == 0,
            $"dotnet pack exited with code {process.ExitCode}.\nStandard output:\n{standardOutput}\nStandard error:\n{standardError}");
    }
}
