namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public int GetUtcNowCallCount { get; private set; }

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);

    public override DateTimeOffset GetUtcNow()
    {
        GetUtcNowCallCount++;
        return _utcNow;
    }
}
