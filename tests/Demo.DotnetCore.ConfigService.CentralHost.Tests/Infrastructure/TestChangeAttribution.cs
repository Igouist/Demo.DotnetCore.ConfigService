namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

// Most tests are not about POC-063 attribution; these overloads supply a fixed one so they keep their focus.
internal static class TestChangeAttribution
{
    public const string ChangedBy = "test-author";

    public static ChangeAttribution Default { get; } = ChangeAttribution.FromValidated(ChangedBy, null);

    public static Task<ConfigurationCommandReceipt> SetAsync(
        this IConfigurationEntryStore store,
        ConfigurationEntryIdentity identity,
        string value,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        store.SetAsync(identity, value, Default, createdAtUtc, cancellationToken);

    public static Task<RemoveStoreResult> RemoveAsync(
        this IConfigurationEntryStore store,
        ConfigurationEntryIdentity identity,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken) =>
        store.RemoveAsync(identity, Default, createdAtUtc, cancellationToken);
}
