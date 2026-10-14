using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Demo.DotnetCore.ConfigService.Client;

internal enum MissingBehavior
{
    CentralRequired,
    FallbackAllowed
}

internal sealed record SelectedConfigurationKey(string DeclaredKey, MissingBehavior MissingBehavior);

internal sealed class KeySelection
{
    private readonly HashSet<string> _membership;

    private KeySelection(IReadOnlyList<SelectedConfigurationKey> keys)
    {
        Keys = new ReadOnlyCollection<SelectedConfigurationKey>(keys.ToArray());
        RequiredKeys = new ReadOnlyCollection<SelectedConfigurationKey>(
            keys.Where(key => key.MissingBehavior == MissingBehavior.CentralRequired).ToArray());
        FallbackKeys = new ReadOnlyCollection<SelectedConfigurationKey>(
            keys.Where(key => key.MissingBehavior == MissingBehavior.FallbackAllowed).ToArray());
        RequestKeys = new ReadOnlyCollection<string>(keys.Select(key => key.DeclaredKey).ToArray());
        _membership = new HashSet<string>(RequestKeys, StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<SelectedConfigurationKey> Keys { get; }

    internal IReadOnlyList<SelectedConfigurationKey> RequiredKeys { get; }

    internal IReadOnlyList<SelectedConfigurationKey> FallbackKeys { get; }

    internal IReadOnlyList<string> RequestKeys { get; }

    internal bool Contains(string key) => _membership.Contains(key);

    internal static bool TryCreate(
        IReadOnlyList<string?> required,
        IReadOnlyList<string?> fallback,
        [NotNullWhen(true)]
        out KeySelection? selection)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<SelectedConfigurationKey>(required.Count + fallback.Count);

        if (!TryAdd(required, MissingBehavior.CentralRequired, seen, keys)
            || !TryAdd(fallback, MissingBehavior.FallbackAllowed, seen, keys)
            || keys.Count == 0)
        {
            selection = null;
            return false;
        }

        selection = new KeySelection(keys);
        return true;
    }

    private static bool TryAdd(
        IEnumerable<string?> candidates,
        MissingBehavior behavior,
        HashSet<string> seen,
        List<SelectedConfigurationKey> keys)
    {
        foreach (string? candidate in candidates)
        {
            if (!ConfigurationKeyRules.IsValid(candidate) || !seen.Add(candidate!))
            {
                return false;
            }

            keys.Add(new SelectedConfigurationKey(candidate!, behavior));
        }

        return true;
    }
}
