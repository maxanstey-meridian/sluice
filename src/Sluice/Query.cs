namespace Sluice;

/// <summary>
/// A named cached value: <typeparamref name="TKey"/> in, <typeparamref name="TValue"/> out.
/// Its cache entry key is <c>&lt;name&gt;:&lt;key text&gt;</c>; rename the query to version it.
/// </summary>
public sealed class Query<TKey, TValue>
    where TKey : notnull
{
    private const string ReservedPrefix = "__fc";

    private readonly Func<TKey, string> _format;

    /// <summary>
    /// Declares a query whose key is a <c>string</c> or <see cref="IFormattable"/>, formatted with the invariant culture.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is blank, contains ':' or starts with <c>__fc</c>, or is already declared with other types, or <typeparamref name="TKey"/>
    /// is an interface or abstract type, is neither <c>string</c> nor <see cref="IFormattable"/>, or is <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/> or <see cref="TimeOnly"/> (pass a format function instead).
    /// </exception>
    public Query(string name)
        : this(name, KeyText.Default<TKey>()) { }

    /// <summary>Declares a query whose key text is produced by <paramref name="format"/>, which must give different text for different keys.</summary>
    /// <exception cref="ArgumentException">
    /// The name is blank, contains ':' or starts with <c>__fc</c>, or is already declared with other types.
    /// </exception>
    public Query(string name, Func<TKey, string> format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(format);
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

        QueryNames.Claim(name, typeof(Query<TKey, TValue>));

        Name = name;
        _format = format;
    }

    /// <summary>
    /// How long an entry lives unless invalidated; <see langword="null"/> uses the cache's default duration.
    /// As in FusionCache, the cache's <c>MemoryCacheDuration</c> and <c>DistributedCacheDuration</c>, when set,
    /// still win at their level.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    internal string Name { get; }

    internal string EntryKey(TKey key) => $"{Name}:{KeyText.Of(_format, key)}";
}
