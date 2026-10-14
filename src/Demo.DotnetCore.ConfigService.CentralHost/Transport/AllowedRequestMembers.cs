namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class AllowedRequestMembers
{
    public static IReadOnlySet<string> Set { get; } =
        new HashSet<string>(["environment", "key", "value"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Remove { get; } =
        new HashSet<string>(["environment", "key"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Resolve { get; } =
        new HashSet<string>(["environment", "keys"], StringComparer.OrdinalIgnoreCase);
}
