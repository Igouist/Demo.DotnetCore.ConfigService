namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;

internal sealed class TemporaryDirectory : IAsyncDisposable
{
    private TemporaryDirectory(string path) => Path = path;

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"central-host-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
