using System.Text.RegularExpressions;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Conventions;

// Central Host logs complete exceptions, so its own exception messages must stay fixed text that cannot carry
// configuration values. This source scan is a heuristic guard against composed messages, not a proof.
public sealed partial class ExceptionMessageConventionTests
{
    [Fact]
    public void Central_Host_exception_constructions_use_only_fixed_messages()
    {
        string sourceRoot = Path.Combine(FindRepositoryRoot(), "src", "Demo.DotnetCore.ConfigService.CentralHost");
        string[] sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(sourceRoot, path))
            .ToArray();
        Assert.NotEmpty(sourceFiles);

        string[] violations = sourceFiles
            .SelectMany(path => ExceptionConstruction().Matches(File.ReadAllText(path))
                .Where(match => ComposedText().IsMatch(match.Value))
                .Select(match => $"{Path.GetRelativePath(sourceRoot, path)}: {match.Value.Trim()}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("throw new InvalidOperationException($\"Key {key} failed.\");")]
    [InlineData("throw new InvalidOperationException(\"Value \" + value);")]
    [InlineData("throw new ArgumentException(string.Format(\"{0}\", value));")]
    public void The_scan_detects_composed_exception_messages(string source) =>
        Assert.Contains(
            ExceptionConstruction().Matches(source),
            match => ComposedText().IsMatch(match.Value));

    private static bool IsBuildOutput(string sourceRoot, string path)
    {
        string firstSegment = Path.GetRelativePath(sourceRoot, path).Split(Path.DirectorySeparatorChar)[0];
        return firstSegment is "bin" or "obj";
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Demo.DotnetCore.ConfigService.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }

    // From an exception construction to the end of its statement.
    [GeneratedRegex(@"new\s+\w*Exception\s*\([^;]*;", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionConstruction();

    // Interpolated strings, string.Format/Concat, or concatenation with a string literal.
    [GeneratedRegex(@"\$@?""|@\$""|string\.(Format|Concat)\s*\(|""\s*\+|\+\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex ComposedText();
}
