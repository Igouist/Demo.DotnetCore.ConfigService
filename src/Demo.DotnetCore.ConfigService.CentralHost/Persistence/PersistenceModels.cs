namespace Demo.DotnetCore.ConfigService.CentralHost;

internal abstract record RemoveStoreResult
{
    private RemoveStoreResult() { }

    internal sealed record Created(ConfigurationCommandReceipt Receipt) : RemoveStoreResult;

    internal sealed record NotFound : RemoveStoreResult;

    internal sealed record AlreadyRemoved : RemoveStoreResult;
}

internal sealed class StoredConfigurationEntry
{
    public long Id { get; init; }

    public string Environment { get; init; } = string.Empty;

    public string ConfigurationKey { get; init; } = string.Empty;
}

internal sealed class StoredLatestVersion
{
    public long Version { get; init; }

    public string Operation { get; init; } = string.Empty;
}

internal sealed class StoredReadVersion
{
    public string? Environment { get; init; }

    public string? ConfigurationKey { get; init; }

    public long Version { get; init; }

    public string? Operation { get; init; }

    public string? Value { get; init; }

    public string? CreatedAtUtc { get; init; }
}

internal sealed class StoredEntrySummaryRow
{
    public string? Environment { get; init; }

    public string? ConfigurationKey { get; init; }

    public long? Version { get; init; }

    public string? Operation { get; init; }

    public string? Value { get; init; }

    public string? CreatedAtUtc { get; init; }
}

internal sealed class StoredResolutionRow
{
    public string? ConfigurationKey { get; init; }

    public long Version { get; init; }

    public string? Operation { get; init; }

    public string? Value { get; init; }
}
