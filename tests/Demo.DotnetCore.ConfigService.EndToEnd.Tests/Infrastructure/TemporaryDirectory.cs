namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed class TemporaryDirectory : IAsyncDisposable
{
    private readonly string prefix;
    private readonly string tempRoot;
    private bool disposed;

    private TemporaryDirectory(string path, string prefix, string tempRoot)
    {
        Path = path;
        this.prefix = prefix;
        this.tempRoot = tempRoot;
    }

    internal string Path { get; }

    internal static TemporaryDirectory Create(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)
            || prefix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || prefix.Contains(System.IO.Path.DirectorySeparatorChar)
            || prefix.Contains(System.IO.Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A temporary-directory prefix must be a non-empty file-name prefix.", nameof(prefix));
        }

        string tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        string path = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(tempRoot, $"{prefix}{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);

        return new TemporaryDirectory(path, prefix, tempRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        EnsureOwnedPath();

        if (!Directory.Exists(Path))
        {
            disposed = true;
            return;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                disposed = true;
                return;
            }
            catch (Exception exception) when (
                attempt < 4
                && (exception is IOException || exception is UnauthorizedAccessException))
            {
                await Task.Delay(TimeSpan.FromMilliseconds((attempt + 1) * 100));
            }
        }
    }

    private void EnsureOwnedPath()
    {
        string fullPath = System.IO.Path.GetFullPath(Path);
        string fullTempRoot = System.IO.Path.GetFullPath(tempRoot);
        string tempRootPrefix = System.IO.Path.TrimEndingDirectorySeparator(fullTempRoot)
            + System.IO.Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(fullPath));

        if (!fullPath.StartsWith(tempRootPrefix, comparison)
            || !leaf.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unowned temporary directory '{fullPath}'.");
        }
    }
}
