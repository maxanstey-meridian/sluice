using System.Collections.Concurrent;

namespace Sluice;

/// <summary>
/// The duplicate <see cref="Query{TKey, TValue}"/> name guard. Nothing ever reads a declaration back from
/// here. Source names aren't checked: a clash there only over-invalidates.
/// </summary>
internal static class QueryNames
{
    private static readonly ConcurrentDictionary<string, Type> _queries = new(
        StringComparer.Ordinal
    );

    internal static void Claim(string name, Type query)
    {
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
