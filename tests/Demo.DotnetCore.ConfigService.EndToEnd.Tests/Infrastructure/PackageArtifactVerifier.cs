using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record PackagedDemoEvidence(
    string PackageVersion,
    string PackagePath,
    string DemoAssemblyPath,
    string AssetsPath,
    string DependencyManifestPath,
    string ResolvedClientAssemblyPath,
    string BuiltClientSha256,
    string PackagedClientSha256,
    string ResolvedClientSha256);

internal static class PackageArtifactVerifier
{
    private const string ClientAssemblyName = "Demo.DotnetCore.ConfigService.Client.dll";
    private const string ClientPackageId = "Demo.DotnetCore.ConfigService.Client";
    private const string PackagedClientEntryPath = "lib/net8.0/Demo.DotnetCore.ConfigService.Client.dll";

    internal static PackagedDemoEvidence Verify(
        string packageVersion,
        string packagePath,
        string builtClientAssemblyPath,
        string primaryPackagePath,
        string demoAssemblyPath,
        string assetsPath,
        string dependencyManifestPath)
    {
        string fullPackagePath = RequireFile("Client package", packagePath);
        string fullBuiltClientPath = RequireFile("isolated built Client assembly", builtClientAssemblyPath);
        string fullDemoAssemblyPath = RequireFile("packaged Demo assembly", demoAssemblyPath);
        string fullAssetsPath = RequireFile("packaged Demo assets", assetsPath);
        string fullDependencyManifestPath = RequireFile(
            "packaged Demo dependency manifest",
            dependencyManifestPath);
        string fullPrimaryPackagePath = RequireDirectory("primary package root", primaryPackagePath);

        string packageLibraryKey = $"{ClientPackageId}/{packageVersion}";
        string builtClientSha256 = HashFile("isolated built Client assembly", fullBuiltClientPath);
        string packagedClientSha256 = HashPackagedClient(fullPackagePath);
        if (!string.Equals(builtClientSha256, packagedClientSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Client package artifact '{fullPackagePath}' does not match the isolated built Client assembly '{fullBuiltClientPath}'.");
        }

        string resolvedClientAssemblyPath = Path.GetFullPath(Path.Combine(
            fullPrimaryPackagePath,
            ClientPackageId.ToLowerInvariant(),
            packageVersion.ToLowerInvariant(),
            "lib",
            "net8.0",
            ClientAssemblyName));
        string primaryPrefix = Path.TrimEndingDirectorySeparator(fullPrimaryPackagePath)
            + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolvedClientAssemblyPath.StartsWith(primaryPrefix, pathComparison))
        {
            throw new InvalidDataException(
                $"Resolved Client artifact '{resolvedClientAssemblyPath}' is outside the primary package root '{fullPrimaryPackagePath}'.");
        }

        resolvedClientAssemblyPath = RequireFile(
            "resolved Client package assembly",
            resolvedClientAssemblyPath);
        string resolvedClientSha256 = HashFile(
            "resolved Client package assembly",
            resolvedClientAssemblyPath);
        if (!string.Equals(builtClientSha256, resolvedClientSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resolved Client package artifact '{resolvedClientAssemblyPath}' does not match the isolated built Client assembly '{fullBuiltClientPath}'.");
        }

        RequirePackageLibraryType("packaged Demo assets", fullAssetsPath, packageLibraryKey);
        RequirePackageLibraryType(
            "packaged Demo dependency manifest",
            fullDependencyManifestPath,
            packageLibraryKey);

        return new PackagedDemoEvidence(
            packageVersion,
            fullPackagePath,
            fullDemoAssemblyPath,
            fullAssetsPath,
            fullDependencyManifestPath,
            resolvedClientAssemblyPath,
            builtClientSha256,
            packagedClientSha256,
            resolvedClientSha256);
    }

    private static string HashPackagedClient(string packagePath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            ZipArchiveEntry[] entries = archive.Entries
                .Where(entry => string.Equals(
                    entry.FullName,
                    PackagedClientEntryPath,
                    StringComparison.Ordinal))
                .ToArray();
            if (entries.Length != 1)
            {
                throw new InvalidDataException(
                    $"Client package artifact '{packagePath}' must contain exactly one '{PackagedClientEntryPath}' entry; found {entries.Length}.");
            }

            using Stream stream = entries[0].Open();
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (InvalidDataException exception) when (!exception.Message.Contains(packagePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Client package artifact '{packagePath}' is not a readable NuGet archive.",
                exception);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Client package artifact '{packagePath}' could not be read.",
                exception);
        }
    }

    private static string HashFile(string artifactName, string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The {artifactName} '{path}' could not be hashed.", exception);
        }
    }

    private static void RequirePackageLibraryType(string artifactName, string path, string libraryKey)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries)
                || libraries.ValueKind != JsonValueKind.Object
                || !libraries.TryGetProperty(libraryKey, out JsonElement library)
                || library.ValueKind != JsonValueKind.Object
                || !library.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "package", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The {artifactName} '{path}' does not identify '{libraryKey}' as type 'package'.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {artifactName} '{path}' is not valid JSON.", exception);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The {artifactName} '{path}' could not be read.", exception);
        }
    }

    private static string RequireFile(string artifactName, string path)
    {
        string fullPath = GetFullPath(artifactName, path);
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException($"The {artifactName} '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static string RequireDirectory(string artifactName, string path)
    {
        string fullPath = GetFullPath(artifactName, path);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidDataException($"The {artifactName} '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static string GetFullPath(string artifactName, string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            || exception is NotSupportedException
            || exception is PathTooLongException)
        {
            throw new InvalidDataException($"The {artifactName} path '{path}' is invalid.", exception);
        }
    }
}
