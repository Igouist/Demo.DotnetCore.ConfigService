namespace Demo.DotnetCore.ConfigService.Client;

internal interface IRetryJitter
{
    TimeSpan Next();
}
