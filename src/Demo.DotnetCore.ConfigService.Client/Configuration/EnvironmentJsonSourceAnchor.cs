using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Demo.DotnetCore.ConfigService.Client;

internal static class EnvironmentJsonSourceAnchor
{
    internal static bool TryFindIndex(
        IList<IConfigurationSource> sources,
        string hostEnvironmentName,
        out int index)
    {
        string expectedPath = $"appsettings.{hostEnvironmentName}.json";
        index = -1;

        for (int candidateIndex = 0; candidateIndex < sources.Count; candidateIndex++)
        {
            if (sources[candidateIndex] is not JsonConfigurationSource jsonSource
                || !string.Equals(jsonSource.Path, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index >= 0)
            {
                index = -1;
                return false;
            }

            index = candidateIndex;
        }

        return index >= 0;
    }
}
