namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

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
