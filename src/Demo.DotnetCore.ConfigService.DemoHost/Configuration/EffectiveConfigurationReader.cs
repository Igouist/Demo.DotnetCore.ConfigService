using Microsoft.Extensions.Configuration;

namespace Demo.DotnetCore.ConfigService.DemoHost;

public sealed class EffectiveConfigurationReader
{
    private const string DemoSectionName = "Demo";
    private readonly IConfiguration _configuration;

    public EffectiveConfigurationReader(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    public IReadOnlyDictionary<string, string> ReadDemoValues() =>
        Materialize(_configuration.GetSection(DemoSectionName).AsEnumerable(makePathsRelative: true));

    public IReadOnlyDictionary<string, string> ReadRootValues() =>
        Materialize(_configuration.AsEnumerable());

    private static IReadOnlyDictionary<string, string> Materialize(
        IEnumerable<KeyValuePair<string, string?>> entries)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, string? value) in entries)
        {
            if (value is null || string.IsNullOrEmpty(path))
            {
                continue;
            }

            values[path] = value;
        }

        return values;
    }
}
