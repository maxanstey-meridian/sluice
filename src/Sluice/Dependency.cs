using System.Diagnostics;

namespace Sluice;

/// <summary>
/// Something a cached value can depend on: one key of a <see cref="Source{TKey}"/> or the whole source.
/// Obtain it from <see cref="Source{TKey}.For"/> or <see cref="Source{TKey}.All"/>, record it with
/// <c>read.From</c> and invalidate it with <see cref="SluiceCache.InvalidateAsync(Dependency, CancellationToken)"/>.
/// </summary>
[DebuggerDisplay("{Text}")]
public sealed class Dependency
{
    private readonly string _source;
    private readonly string? _key;

    internal Dependency(string source, string? key)
    {
        _source = source;
        _key = key;
    }

    internal string Text => _key is null ? $"{_source} (all)" : $"{_source}:{_key}";

    // A key's readers carry "n:k" and "n:", a whole-source reader "n:*" and "n:". Invalidating a key hits "n:k"
    // and "n:*"; invalidating the source hits "n:". Every tag contains ':', so none can equal a FusionCache
    // clear sentinel.
    internal string[] ReadTags =>
        _key is null ? [$"{_source}:*", $"{_source}:"] : [$"{_source}:{_key}", $"{_source}:"];

    internal string[] InvalidationTags =>
        _key is null ? [$"{_source}:"] : [$"{_source}:{_key}", $"{_source}:*"];

    internal static List<string> TagsOf(
        IEnumerable<Dependency> dependencies,
        Func<Dependency, string[]> tags
    )
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        return dependencies
            .SelectMany(dependency =>
                dependency is null
                    ? throw new ArgumentException(
                        "A dependency in the list is null. Pass only values from Source.For or Source.All.",
                        nameof(dependencies)
                    )
                    : tags(dependency)
            )
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
