namespace Sluice;

/// <summary>
/// A named family of dependencies with a typed key, declared once and shared by readers and writers.
/// </summary>
/// <remarks>
/// Key text is part of the wire format: renaming the source, or changing its format,
/// changes every tag, so old and new nodes stop evicting each other's entries until their TTL.
/// A name declared again must keep its key type and format (the same lambda or method), since a write through one
/// declaration would otherwise miss readers of the other; declare each source once and share it. For the same reason a
/// name the EF Core package gives a model's dependencies (a table's, such as <c>Customers</c>) can't be declared here.
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
    /// The name is blank, already declared with another key type or format, or one the EF Core package names a
    /// model's dependencies with; or <typeparamref name="TKey"/> is an interface or abstract type, is neither
    /// <c>string</c> nor <see cref="IFormattable"/>, or is <see cref="DateTime"/>, <see cref="DateTimeOffset"/> or
    /// <see cref="TimeOnly"/> (pass a format function instead).
    /// </exception>
    public Source(string name)
        : this(name, KeyText.Default<TKey>()) { }

    /// <summary>Declares a source whose key text is produced by <paramref name="format"/>, which must give different text for different keys.</summary>
    /// <remarks>A compiled expression's format isn't compared with another declaration's.</remarks>
    /// <exception cref="ArgumentException">
    /// The name is blank, already declared with another key type or another format, or one the EF Core package names
    /// a model's dependencies with.
    /// </exception>
    public Source(string name, Func<TKey, string> format)
        : this(name, format, model: false) { }

    // A package whose names are unique by construction, and shared on purpose, builds its sources through
    // Infrastructure.ModelSource: they aren't checked against each other, but no application source can share a name
    // with one.
    private Source(string name, Func<TKey, string> format, bool model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(format);
        if (model)
        {
            SourceNames.ClaimForModel(name);
        }
        else
        {
            SourceNames.Claim(name, typeof(TKey), format);
        }

        _name = name;
        _format = format;
        All = new Dependency(name, null);
    }

    internal static Source<TKey> ForModel(string name, Func<TKey, string> format) =>
        new(name, format, model: true);

    /// <summary>The dependency on one key of this source.</summary>
    public Dependency For(TKey key) => new(_name, KeyText.Of(_format, key));

    /// <summary>The dependency on everything in this source.</summary>
    public Dependency All { get; }
}
