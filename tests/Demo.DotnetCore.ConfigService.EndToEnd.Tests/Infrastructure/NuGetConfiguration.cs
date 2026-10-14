using System.Xml.Linq;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal static class NuGetConfiguration
{
    private const string ClientPackageId = "Demo.DotnetCore.ConfigService.Client";

    internal static void Write(
        string path,
        string packageFeedPath,
        string? fallbackPackagePath)
    {
        var fallback = new XElement("fallbackPackageFolders", new XElement("clear"));
        if (!string.IsNullOrWhiteSpace(fallbackPackagePath)
            && Directory.Exists(fallbackPackagePath))
        {
            fallback.Add(new XElement(
                "add",
                new XAttribute("key", "pre-restored"),
                new XAttribute("value", Path.GetFullPath(fallbackPackagePath))));
        }

        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement(
                        "add",
                        new XAttribute("key", "demo-client"),
                        new XAttribute("value", Path.GetFullPath(packageFeedPath))),
                    new XElement(
                        "add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json"),
                        new XAttribute("protocolVersion", "3"))),
                fallback,
                new XElement(
                    "packageSourceMapping",
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "demo-client"),
                        new XElement("package", new XAttribute("pattern", ClientPackageId))),
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "nuget.org"),
                        new XElement("package", new XAttribute("pattern", "*"))))));

        document.Save(path);
    }
}
