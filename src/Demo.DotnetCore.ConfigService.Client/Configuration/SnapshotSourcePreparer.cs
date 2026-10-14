namespace Demo.DotnetCore.ConfigService.Client;

internal interface ISnapshotSourcePreparer
{
    CentralConfigSnapshotSource Prepare(
        IReadOnlyDictionary<string, string> values);
}

internal sealed class SnapshotSourcePreparer : ISnapshotSourcePreparer
{
    internal static SnapshotSourcePreparer Instance { get; } = new();

    private SnapshotSourcePreparer()
    {
    }

    public CentralConfigSnapshotSource Prepare(
        IReadOnlyDictionary<string, string> values) =>
        new(values);
}
