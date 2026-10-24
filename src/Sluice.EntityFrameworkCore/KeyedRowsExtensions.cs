namespace Sluice;

/// <summary>
/// Names the rows a bulk write is about, so once it commits Sluice evicts only their readers:
/// <c>db.Set&lt;Form&gt;().Where(f =&gt; !f.IsLive).ForKey(id).ExecuteUpdateAsync(s =&gt; s.SetProperty(f =&gt; f.IsLive,
/// true), ct)</c>. Sluice adds the key condition to the statement itself, ANDed onto the query's own <c>Where</c>, so a
/// conditional write stays conditional and can only touch those rows. It doesn't recognise a key condition in the
/// app's own <c>Where</c>: move it into <c>ForKey</c>.
/// </summary>
public static class KeyedRowsExtensions
{
    /// <summary>
    /// The rows <paramref name="rows"/> selects whose primary key is <paramref name="key"/>: at most one. Call it last,
    /// just before <c>ExecuteUpdate</c> or <c>ExecuteDelete</c>.
    /// </summary>
    /// <param name="rows">An EF query of <typeparamref name="T"/>, on a context configured with <c>UseSluice</c>.</param>
    /// <param name="key">The primary key, of its CLR type in the EF model.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> isn't an EF query of an entity type, or <paramref name="key"/> isn't of the primary
    /// key's CLR type.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table, or its primary key is composite.
    /// </exception>
    public static KeyedRows<T> ForKey<T>(this IQueryable<T> rows, object key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);

        return KeyedRows<T>.Of(rows, [key], nameof(key));
    }

    /// <summary>
    /// The rows <paramref name="rows"/> selects whose primary key is one of <paramref name="keys"/>. Call it last,
    /// just before <c>ExecuteUpdate</c> or <c>ExecuteDelete</c>. Past 100 keys, the write evicts every read of
    /// <typeparamref name="T"/>, as a plain bulk write does; with none, it runs nothing and returns 0.
    /// </summary>
    /// <param name="rows">An EF query of <typeparamref name="T"/>, on a context configured with <c>UseSluice</c>.</param>
    /// <param name="keys">
    /// The primary keys, each of its CLR type in the EF model; read once, here, and a key given twice counts once.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> isn't an EF query of an entity type, or a key is null or isn't of the primary key's CLR
    /// type.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table, or its primary key is composite.
    /// </exception>
    public static KeyedRows<T> ForKeys<T, TKey>(this IQueryable<T> rows, IEnumerable<TKey> keys)
        where T : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keys);

        object[] values =
        [
            .. keys.Select(key =>
                    key is null
                        ? throw new ArgumentException("A key in the list is null.", nameof(keys))
                        : (object)key
                )
                .Distinct(),
        ];
        return KeyedRows<T>.Of(rows, values, nameof(keys));
    }
}
