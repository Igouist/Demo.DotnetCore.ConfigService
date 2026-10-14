namespace Demo.DotnetCore.ConfigService.DemoHost.Tests.Infrastructure;

internal static class RepositoryPaths
{
    internal static string FindRoot()
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
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
