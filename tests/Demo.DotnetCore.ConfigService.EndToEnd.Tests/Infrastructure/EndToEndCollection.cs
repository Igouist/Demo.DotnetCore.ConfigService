namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EndToEndCollection : ICollectionFixture<PackagedDemoFixture>
{
    public const string Name = "Config Distribution E2E";
}
