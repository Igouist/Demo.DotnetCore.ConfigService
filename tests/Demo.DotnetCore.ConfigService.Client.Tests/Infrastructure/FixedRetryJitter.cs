namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

// Each retry dequeues one value, so a test expecting N retries must supply at least N values. Running out makes
// Next() throw, which in these tests shows up as a hang rather than a clear failure.
internal sealed class FixedRetryJitter(params TimeSpan[] values) : IRetryJitter
{
    private readonly Queue<TimeSpan> _values = new(values);

    internal int CallCount { get; private set; }

    internal Action? OnNext { get; init; }

    public TimeSpan Next()
    {
        CallCount++;
        TimeSpan value = _values.Dequeue();
        OnNext?.Invoke();
        return value;
    }
}
