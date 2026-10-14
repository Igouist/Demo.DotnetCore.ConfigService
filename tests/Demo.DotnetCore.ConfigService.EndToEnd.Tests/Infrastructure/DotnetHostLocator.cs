namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal static class DotnetHostLocator
{
    internal static string Resolve()
    {
        string? configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost)
            && Path.IsPathRooted(configuredHost)
            && File.Exists(configuredHost))
        {
            return Path.GetFullPath(configuredHost);
        }

        if (IsDotnetHost(Environment.ProcessPath))
        {
            return Path.GetFullPath(Environment.ProcessPath!);
        }

        string? searchPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(searchPath))
        {
            foreach (string segment in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = segment.Trim().Trim('"');
                foreach (string fileName in new[] { "dotnet", "dotnet.exe" })
                {
                    string candidate;
                    try
                    {
                        candidate = Path.GetFullPath(Path.Combine(directory, fileName));
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException
                        || exception is NotSupportedException
                        || exception is PathTooLongException)
                    {
                        continue;
                    }

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        throw new FileNotFoundException("Unable to locate a dotnet host executable.");
    }

    private static bool IsDotnetHost(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }
}
