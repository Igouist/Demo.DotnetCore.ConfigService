namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal static class ProcessEnvironment
{
    private static readonly string[] CommonVariables =
    [
        "DOTNET_ROOT",
        "DOTNET_ROOT(x86)",
        "SystemRoot",
        "WINDIR",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR"
    ];

    private static readonly string[] ProxyVariables =
    [
        "HTTP_PROXY",
        "http_proxy",
        "HTTPS_PROXY",
        "https_proxy",
        "NO_PROXY",
        "no_proxy"
    ];

    internal static IReadOnlyDictionary<string, string> CreateBuild(
        string fixtureRoot,
        string primaryPackagePath,
        string httpCachePath,
        string pluginsCachePath)
    {
        Dictionary<string, string> environment = CreateBase(fixtureRoot);
        Preserve(environment, ProxyVariables);

        if (OperatingSystem.IsWindows())
        {
            string applicationData = Path.GetFullPath(Path.Combine(fixtureRoot, "app-data"));
            string localApplicationData = Path.GetFullPath(Path.Combine(fixtureRoot, "local-app-data"));
            string programFiles = Path.GetFullPath(Path.Combine(fixtureRoot, "program-files"));
            string programFilesX86 = Path.GetFullPath(Path.Combine(fixtureRoot, "program-files-x86"));
            Directory.CreateDirectory(applicationData);
            Directory.CreateDirectory(localApplicationData);
            Directory.CreateDirectory(programFiles);
            Directory.CreateDirectory(programFilesX86);
            environment["APPDATA"] = applicationData;
            environment["LOCALAPPDATA"] = localApplicationData;
            environment["PROGRAMFILES"] = programFiles;
            environment["PROGRAMFILES(X86)"] = programFilesX86;
        }

        string packages = Path.GetFullPath(primaryPackagePath);
        string httpCache = Path.GetFullPath(httpCachePath);
        string pluginsCache = Path.GetFullPath(pluginsCachePath);
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(httpCache);
        Directory.CreateDirectory(pluginsCache);

        environment["NUGET_PACKAGES"] = packages;
        environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
        environment["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache;

        return environment;
    }

    internal static IReadOnlyDictionary<string, string> CreateApplication(
        string scenarioRoot,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        Dictionary<string, string> environment = CreateBase(scenarioRoot);
        environment["DOTNET_ENVIRONMENT"] = "E2E";

        if (overrides is not null)
        {
            foreach ((string name, string value) in overrides)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private static Dictionary<string, string> CreateBase(string ownedRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Preserve(environment, CommonVariables);

        string root = Path.GetFullPath(ownedRoot);
        string temp = Path.Combine(root, "temp");
        string cliHome = Path.Combine(root, "cli-home");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(cliHome);

        environment["HOME"] = root;
        environment["USERPROFILE"] = root;
        environment["TEMP"] = temp;
        environment["TMP"] = temp;
        environment["TMPDIR"] = temp;
        environment["DOTNET_CLI_HOME"] = cliHome;
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        return environment;
    }

    private static void Preserve(IDictionary<string, string> environment, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                environment[name] = value;
            }
        }
    }
}
