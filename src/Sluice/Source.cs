namespace Sluice;

/// <summary>
/// A named family of dependencies with a typed key, declared once and shared by readers and writers.
/// </summary>
/// <remarks>
/// Key text is part of the wire format: renaming the source, or changing its format,
/// changes every tag, so old and new nodes stop evicting each other's entries until their TTL.
/// Source names aren't checked for duplicates; declare each source once.
/// </remarks>
public sealed class Source<TKey>
    where TKey : notnull
{
    private readonly string _name;
    private readonly Func<TKey, string> _format;

    /// <summary>
    /// Declares a source whose key is a <c>string</c> or <see cref="IFormattable"/>, formatted with the invariant culture.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is blank, or <typeparamref name="TKey"/> is neither <c>string</c> nor <see cref="IFormattable"/>,
    /// or is <see cref="DateTime"/>, <see cref="DateTimeOffset"/> or <see cref="TimeOnly"/> (pass a format function instead).
    /// </exception>
    public Source(string name)
        : this(name, KeyText.Default<TKey>()) { }

    /// <summary>Declares a source whose key text is produced by <paramref name="format"/>, which must give different text for different keys.</summary>
    public Source(string name, Func<TKey, string> format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(format);

        _name = name;
        _format = format;
        All = new Dependency(name, null);
    }

    /// <summary>The dependency on one key of this source.</summary>
    public Dependency For(TKey key) => new(_name, KeyText.Of(_format, key));

    /// <summary>The dependency on everything in this source.</summary>
    public Dependency All { get; }
}
