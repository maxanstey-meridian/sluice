using System.Diagnostics;

namespace Sluice;

/// <summary>
/// Something a cached value can depend on: one key of a <see cref="Source{TKey}"/> or the whole source.
/// Obtain it from <see cref="Source{TKey}.For"/> or <see cref="Source{TKey}.All"/>, record it with
/// <c>read.From</c> and invalidate it with <see cref="SluiceCache.InvalidateAsync(Dependency)"/>.
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

    /// <summary>
    /// Refuses a FusionCache clear tag that could equal one of these tags: any with ':' after a non-blank start, as
    /// <c>a:b</c> is source <c>a</c> key <c>b</c>'s. Invalidating that dependency would clear the cache on every node.
    /// </summary>
    /// <exception cref="InvalidOperationException">The clear tag could be a dependency's tag.</exception>
    internal static void EnsureNotATag(string clearTag, string option)
    {
        var colon = clearTag.LastIndexOf(':');
        if (colon > 0 && !string.IsNullOrWhiteSpace(clearTag[..colon]))
        {
            throw new InvalidOperationException(
                $"The Sluice cache's InternalStrings.{option} is '{clearTag}', which could be the tag Sluice uses for "
                    + $"source '{clearTag[..colon]}', so invalidating that source would clear the cache on every node. "
                    + "Use a clear tag without ':': leave InternalStrings at its defaults, or call SetToSafeStrings."
            );
        }
    }

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
