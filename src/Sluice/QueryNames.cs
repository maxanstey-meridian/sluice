using System.Reflection;

namespace Sluice;

/// <summary>
/// The query name guard, for keyed <see cref="Query{TKey, TValue}"/> and keyless <see cref="Query{TValue}"/> alike:
/// a name must not be blank, contain ':' or start with <c>__fc</c>, and must keep one query type and one key format.
/// Nothing ever reads a declaration back from here. <see cref="SourceNames"/> is the same guard for sources.
/// </summary>
internal static class QueryNames
{
    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, (Type Query, MethodInfo? Format)> _queries = new(
        StringComparer.Ordinal
    );

    private const string ReservedPrefix = "__fc";

    /// <param name="name">The query name.</param>
    /// <param name="query">The query type, generic arguments included.</param>
    /// <param name="format">The key format; null for a keyless query.</param>
    /// <exception cref="ArgumentException">
    /// The name is blank, contains ':' or starts with <c>__fc</c>, or is already declared as another query type or
    /// with another format.
    /// </exception>
    internal static void Claim(string name, Type query, Delegate? format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Query name '{name}' contains ':'. Choose a name without ':' so '<name>:<key>' entry keys stay unambiguous.",
                nameof(name)
            );
        }

        // FusionCache keeps tag data under "__fc:t:<tag>" ("__fc-t-<tag>" after SetToSafeStrings), in the same
        // key space as entries, and treats any key under that prefix as tag data. A query named "__fc" with a key
        // starting "t:" would never be invalidated, and key "t:*" would also overwrite the clear sentinel's data.
        // EnsureTagPrefix keeps the prefix under "__fc", so this keeps every entry key out of it.
        if (name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Query name '{name}' starts with '{ReservedPrefix}', which FusionCache uses for its own keys. Choose another name.",
                nameof(name)
            );
        }

        var method = format is null ? null : FormatIdentity.Of(format);
        lock (_lock)
        {
            if (!_queries.TryGetValue(name, out var existing))
            {
                _queries[name] = (query, method);
                return;
            }

            if (existing.Query != query)
            {
                throw new ArgumentException(
                    $"'{name}' is already declared as {Describe(existing.Query)}, so declaring it as {Describe(query)} "
                        + "would mix its cache entries. Give one of them a different name.",
                    nameof(name)
                );
            }

            // A format that can't be compared (a compiled expression) is passed over; the first one that can is
            // what later declarations must match.
            if (existing.Format is null || method is null)
            {
                _queries[name] = (query, existing.Format ?? method);
                return;
            }

            if (!FormatIdentity.Same(existing.Format, method))
            {
                throw new ArgumentException(
                    $"'{name}' is already declared as {Describe(query)} with another format "
                        + $"({FormatIdentity.Describe(existing.Format)}), so one key could get different text in each, "
                        + "splitting or mixing their entries. Declare the query once and use that declaration everywhere.",
                    nameof(name)
                );
            }
        }
    }

    /// <summary>
    /// Refuses a FusionCache tag key prefix outside <c>__fc</c>. FusionCache keeps a tag's data at
    /// <c>&lt;prefix&gt;&lt;tag&gt;</c>, in the entries' key space, and skips the tag check of any key under the
    /// prefix, so an entry key under it would never be invalidated, and could be overwritten by a tag's marker.
    /// </summary>
    /// <exception cref="InvalidOperationException">The prefix doesn't start with <c>__fc</c>.</exception>
    internal static void EnsureTagPrefix(string prefix)
    {
        if (!prefix.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Sluice cache's InternalStrings.TagCacheKeyPrefix is '{prefix}', which doesn't start with "
                    + $"'{ReservedPrefix}', so a query's entry keys could fall under it: FusionCache would take them for "
                    + "tag data, never invalidate them, and could overwrite one with a tag's marker (query 't' key 'x' "
                    + "under the prefix 't:' is the marker of tag 'x'). Leave InternalStrings at its defaults, or call "
                    + "SetToSafeStrings with '_' as its special character."
            );
        }
    }

    private static string Describe(Type query) =>
        $"Query<{string.Join(", ", query.GetGenericArguments().Select(t => t.ToString()))}>";
}
