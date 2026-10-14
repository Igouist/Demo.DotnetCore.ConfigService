using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Configuration;

public sealed class CentralConfigSnapshotProviderTests
{
    [Fact]
    public void Snapshot_is_defensive_case_insensitive_and_never_reloads()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Demo:Message"] = "central"
        };
        var source = new CentralConfigSnapshotSource(values);
        IConfigurationProvider provider = source.Build(new ConfigurationBuilder());
        provider.Load();
        values["Demo:Message"] = "changed";

        Assert.True(provider.TryGet("demo:message", out string? actual));
        Assert.Equal("central", actual);
        Assert.False(provider.GetReloadToken().HasChanged);
        Assert.Throws<NotSupportedException>(() => provider.Set("Demo:Message", "mutated"));
    }

    [Fact]
    public void Snapshot_preserves_empty_string_values()
    {
        var source = new CentralConfigSnapshotSource(
            new Dictionary<string, string> { ["Demo:OptionalMessage"] = string.Empty });
        IConfigurationProvider provider = source.Build(new ConfigurationBuilder());

        provider.Load();

        Assert.True(provider.TryGet("Demo:OptionalMessage", out string? actual));
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void Prepared_source_build_and_load_do_not_reenumerate_original_values()
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Demo:Message"] = "central",
            ["Demo:Empty"] = string.Empty
        };
        var values = new SingleEnumerationReadOnlyDictionary(original);
        CentralConfigSnapshotSource source = SnapshotSourcePreparer.Instance.Prepare(values);
        original["Demo:Message"] = "changed";
        original["Demo:Late"] = "late";

        IConfigurationProvider provider = source.Build(new ConfigurationBuilder());
        provider.Load();

        Assert.Equal(1, values.EnumerationCount);
        Assert.True(provider.TryGet("demo:message", out string? message));
        Assert.Equal("central", message);
        Assert.True(provider.TryGet("DEMO:EMPTY", out string? empty));
        Assert.Equal(string.Empty, empty);
        Assert.False(provider.TryGet("Demo:Late", out _));
        Assert.Throws<NotSupportedException>(() => provider.Set("Demo:Message", "mutated"));
    }

    [Fact]
    public void Provider_load_uses_prepared_data_without_enumerating_entries()
    {
        var prepared = new EnumerationForbiddenDictionary
        {
            ["Demo:Message"] = "central",
            ["Demo:Empty"] = string.Empty
        };
        var provider = new CentralConfigSnapshotProvider(prepared);

        provider.Load();

        Assert.True(provider.TryGet("demo:message", out string? message));
        Assert.Equal("central", message);
        Assert.True(provider.TryGet("DEMO:EMPTY", out string? empty));
        Assert.Equal(string.Empty, empty);
        Assert.Throws<NotSupportedException>(() => provider.Set("Demo:Message", "mutated"));
    }

    [Fact]
    public void Physical_source_order_produces_low_environment_central_high_precedence()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Message"] = "base" });
        builder.Add(new JsonConfigurationSource
        {
            Path = "appsettings.UAT.json",
            FileProvider = new InMemoryJsonFileProvider("""{"Demo":{"Message":"environment"}}"""u8.ToArray())
        });
        builder.Add(new CentralConfigSnapshotSource(
            new Dictionary<string, string> { ["Demo:Message"] = "central" }));

        IConfigurationRoot configuration = builder.Build();

        Assert.Equal("central", configuration["Demo:Message"]);

        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Message"] = "high" });

        IConfigurationRoot highConfiguration = builder.Build();

        Assert.Equal("high", highConfiguration["Demo:Message"]);
    }

    private sealed class InMemoryJsonFileProvider(byte[] content) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) =>
            string.Equals(subpath, "appsettings.UAT.json", StringComparison.Ordinal)
                ? new InMemoryJsonFileInfo(content)
                : new NotFoundFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class InMemoryJsonFileInfo(byte[] content) : IFileInfo
    {
        public bool Exists => true;

        public long Length => content.Length;

        public string? PhysicalPath => null;

        public string Name => "appsettings.UAT.json";

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream(content, writable: false);
    }

    private sealed class SingleEnumerationReadOnlyDictionary(
        IReadOnlyDictionary<string, string> values) : IReadOnlyDictionary<string, string>
    {
        internal int EnumerationCount { get; private set; }

        public string this[string key] => values[key];

        public IEnumerable<string> Keys => values.Keys;

        public IEnumerable<string> Values => values.Values;

        public int Count => values.Count;

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException("Original snapshot values were enumerated more than once.");
            }

            return values.GetEnumerator();
        }

        public bool TryGetValue(string key, out string value) =>
            values.TryGetValue(key, out value!);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EnumerationForbiddenDictionary :
        Dictionary<string, string?>,
        IEnumerable<KeyValuePair<string, string?>>,
        IEnumerable
    {
        internal EnumerationForbiddenDictionary()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        IEnumerator<KeyValuePair<string, string?>>
            IEnumerable<KeyValuePair<string, string?>>.GetEnumerator() =>
            throw new InvalidOperationException("Prepared snapshot entries must not be enumerated during Load.");

        IEnumerator IEnumerable.GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, string?>>)this).GetEnumerator();
    }
}
