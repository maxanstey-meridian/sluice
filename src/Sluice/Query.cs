namespace Sluice;

/// <summary>
/// A named cached value: <typeparamref name="TKey"/> in, <typeparamref name="TValue"/> out.
/// Its cache entry key is <c>&lt;name&gt;:&lt;key text&gt;</c>; rename the query to version it.
/// </summary>
public sealed class Query<TKey, TValue>
    where TKey : notnull
{
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
        ArgumentNullException.ThrowIfNull(format);
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

    /// <summary>
    /// The size of an entry in Sluice's memory cache, for a memory cache with a <c>SizeLimit</c>;
    /// <see langword="null"/> uses the cache's default (<c>DefaultEntryOptions.Size</c>). In the units the limit
    /// counts, such as one per entry, or an estimate of its bytes. With a limit set, every entry needs a size, so set
    /// this on every query or set the default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is negative.</exception>
    public long? Size
    {
        get;
        init
        {
            if (value is { } size)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(Size));
            }

            field = value;
        }
    }

    internal string Name { get; }

    internal string EntryKey(TKey key) => $"{Name}:{KeyText.Of(_format, key)}";
}

/// <summary>
/// A named cached value with no key: one entry, such as a list of everything. Its cache entry key is its name.
/// </summary>
/// <remarks>
/// Whatever changes what its compute reads, such as a tenant or a locale, belongs in a key: use
/// <see cref="Query{TKey, TValue}"/> for those.
/// </remarks>
public sealed class Query<TValue>
{
    /// <summary>Declares a query with no key.</summary>
    /// <exception cref="ArgumentException">
    /// The name is blank, contains ':' or starts with <c>__fc</c>, or is already declared with other types.
    /// </exception>
    public Query(string name)
    {
        QueryNames.Claim(name, typeof(Query<TValue>));
        Name = name;
    }

    /// <inheritdoc cref="Query{TKey, TValue}.Duration"/>
    public TimeSpan? Duration { get; init; }

    /// <inheritdoc cref="Query{TKey, TValue}.Size"/>
    public long? Size
    {
        get;
        init
        {
            if (value is { } size)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(Size));
            }

            field = value;
        }
    }

    internal string Name { get; }
}
