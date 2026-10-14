using System.Text;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class ConfigurationValueLimits
{
    // POC-050 bounds one scalar value so it cannot become an unbounded document payload.
    public const int MaxConfigurationValueUtf8Bytes = 16 * 1024;

    public static bool IsValid(string? value) =>
        value is not null && Encoding.UTF8.GetByteCount(value) <= MaxConfigurationValueUtf8Bytes;
}
