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
    /// The name is blank or contains ':', or is already declared with other types, or <typeparamref name="TKey"/>
    /// is neither <c>string</c> nor <see cref="IFormattable"/>, or is <see cref="DateTime"/>, <see cref="DateTimeOffset"/>
    /// or <see cref="TimeOnly"/> (pass a format function instead).
    /// </exception>
    public Query(string name)
        : this(name, KeyText.Default<TKey>()) { }

    /// <summary>Declares a query whose key text is produced by <paramref name="format"/>, which must give different text for different keys.</summary>
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

        Declarations.Claim(name, typeof(Query<TKey, TValue>));

        Name = name;
        _format = format;
    }

    /// <summary>How long an entry lives unless invalidated; <see langword="null"/> uses the cache's default duration.</summary>
    public TimeSpan? Duration { get; init; }

    internal string Name { get; }

    internal string EntryKey(TKey key) => $"{Name}:{KeyText.Of(_format, key)}";
}
