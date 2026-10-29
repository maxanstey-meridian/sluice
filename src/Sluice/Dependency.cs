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

    // Whether its write publishes hard (SLUICE-EF-028): set only on a new instance, by AsNow, never on one already
    // handed out, since Source.All is one instance every reader and writer of the source shares.
    private readonly bool _now;

    internal Dependency(string source, string? key, bool now = false)
    {
        _source = source;
        _key = key;
        _now = now;
    }

    internal string Text => _key is null ? $"{_source} (all)" : $"{_source}:{_key}";

    // A key's readers carry "n:k" and "n:", a whole-source reader "n:*" and "n:". Invalidating a key hits "n:k"
    // and "n:*"; invalidating the source hits "n:". Every tag contains ':', so none can equal a FusionCache
    // clear sentinel.
    internal string[] ReadTags =>
        _key is null ? [$"{_source}:*", $"{_source}:"] : [$"{_source}:{_key}", $"{_source}:"];

    internal string[] InvalidationTags =>
        _key is null ? [$"{_source}:"] : [$"{_source}:{_key}", $"{_source}:*"];

    // What a hard eviction publishes: the invalidation tags and their now-tags, so it reaches stale copies too. Built from
    // the plain tags, which a hard dependency keeps, so it never yields a '!!' form.
    internal string[] HardInvalidationTags => [.. InvalidationTags, .. NowTags(InvalidationTags)];

    // What InvalidateAsync, Invalidate, and a package's publish publish for it: the hard tags for a dependency AsNow
    // marked, otherwise the plain ones. InvalidationTags stays plain either way, since SLUICE-CORE-019's overlap check
    // reads it as the invalidation rule, and a read of a source named '!x' must not overlap a hard write of 'x'.
    internal string[] PublishedTags => _now ? HardInvalidationTags : InvalidationTags;

    // The same dependency, published hard wherever and whenever it's published: itself when it already is, otherwise a
    // new instance, so a shared one stays as it was.
    internal Dependency AsNow() => _now ? this : new(_source, _key, now: true);

    // Each of the dependencies AsNow when now, otherwise the list itself: what a write started inside
    // SluiceFlow.InvalidateNow derives.
    internal static List<Dependency> AsNow(List<Dependency> dependencies, bool now) =>
        now ? [.. dependencies.Select(dependency => dependency.AsNow())] : dependencies;

    // A stale copy (SLUICE-CORE-024) carries each tag its compute read prefixed '!', so InvalidateAsync, which publishes
    // only the plain tags, leaves it, and only a hard eviction reaches it. A now-tag keeps its ':' after a non-blank
    // start, so none can equal a clear sentinel either.
    internal static string[] NowTags(string[] tags) => [.. tags.Select(tag => $"!{tag}")];

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
