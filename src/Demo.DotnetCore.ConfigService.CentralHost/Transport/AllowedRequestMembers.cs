namespace Demo.DotnetCore.ConfigService.CentralHost;

internal static class AllowedRequestMembers
{
    public static IReadOnlySet<string> Set { get; } =
        new HashSet<string>(["environment", "key", "value", "changedBy", "reason"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Remove { get; } =
        new HashSet<string>(["environment", "key", "changedBy", "reason"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Resolve { get; } =
        new HashSet<string>(["environment", "keys"], StringComparer.OrdinalIgnoreCase);
}
