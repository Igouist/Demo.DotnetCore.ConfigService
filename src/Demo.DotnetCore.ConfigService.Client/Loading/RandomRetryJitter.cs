namespace Demo.DotnetCore.ConfigService.Client;

internal sealed class RandomRetryJitter : IRetryJitter
{
    internal static RandomRetryJitter Instance { get; } = new();

    private RandomRetryJitter()
    {
    }

    public TimeSpan Next() => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
}
