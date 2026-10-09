using System.Collections.Concurrent;

namespace Sluice;

/// <summary>
/// The query name guard, for keyed <see cref="Query{TKey, TValue}"/> and keyless <see cref="Query{TValue}"/> alike:
/// a name must not be blank, contain ':' or start with <c>__fc</c>, and must keep one query type. Nothing ever reads
/// a declaration back from here. Source names aren't checked: a clash there only over-invalidates.
/// </summary>
internal static class QueryNames
{
    private static readonly ConcurrentDictionary<string, Type> _queries = new(
        StringComparer.Ordinal
    );

    private const string ReservedPrefix = "__fc";

    /// <exception cref="ArgumentException">
    /// The name is blank, contains ':' or starts with <c>__fc</c>, or is already declared as another query type.
    /// </exception>
    internal static void Claim(string name, Type query)
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
        if (name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Query name '{name}' starts with '{ReservedPrefix}', which FusionCache uses for its own keys. Choose another name.",
                nameof(name)
            );
        }

        var existing = _queries.GetOrAdd(name, query);
        if (existing != query)
        {
            throw new ArgumentException(
                $"'{name}' is already declared as {Describe(existing)}, so declaring it as {Describe(query)} "
                    + "would mix its cache entries. Give one of them a different name.",
                nameof(name)
            );
        }
    }

    private static string Describe(Type query) =>
        $"Query<{string.Join(", ", query.GetGenericArguments().Select(t => t.ToString()))}>";
}
