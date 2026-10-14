using System.Text;

namespace Demo.DotnetCore.ConfigService.Client;

internal static class ConfigurationValueLimits
{
    // POC-050 bounds one scalar value to keep its retained and transported cost finite.
    internal const int MaxConfigurationValueUtf8Bytes = 16 * 1024;

    internal static bool IsValid(string value) =>
        Encoding.UTF8.GetByteCount(value) <= MaxConfigurationValueUtf8Bytes;
}
