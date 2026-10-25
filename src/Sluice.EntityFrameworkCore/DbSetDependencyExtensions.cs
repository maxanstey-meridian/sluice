using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>
/// The dependencies Sluice derives from the EF model, for invalidating after a write Sluice doesn't see: raw SQL
/// (on any context, <c>UseSluice</c> or not), another service, or a context without <c>UseSluice</c>. Pass them to
/// <see cref="SluiceCache.InvalidateAsync(IEnumerable{Dependency})"/> after the write commits; they batch with
/// dependencies from declared sources.
/// </summary>
public static class DbSetDependencyExtensions
{
    /// <summary>
    /// The dependency on the <typeparamref name="T"/> row with primary key <paramref name="key"/>. Invalidating it
    /// evicts <see cref="SluiceReadsExtensions.Entity{T}(SluiceReads, DbSet{T}, object)"/> readers of that row, by its
    /// key or by a unique column that found it, and other queries that read <typeparamref name="T"/>, but not
    /// <see cref="SluiceReadsExtensions.Children"/> readers of the row's parent, nor readers by a unique value no row
    /// held: Sluice can't see the row's foreign keys or new column values. After a bypass that changed such rows, use
    /// <see cref="AllDependencies"/>.
    /// </summary>
    /// <param name="set">The set the row belongs to.</param>
    /// <param name="key">The primary key, of its CLR type in the EF model.</param>
    /// <returns>
    /// What <see cref="SluiceReadsExtensions.Entity{T}(SluiceReads, DbSet{T}, object)"/> records for the same key.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't of the primary key's CLR type.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table, or its primary key is composite.
    /// </exception>
    public static Dependency Dependency<T>(this DbSet<T> set, object key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);

        ModelSources.NameProvider(set);
        return ModelSources.EntityDependency(set.EntityType, key, nameof(key));
    }

    /// <summary>
    /// Every dependency on <typeparamref name="T"/>: the whole set, and every foreign key and unique column it, a base
    /// or a derived type declares, so every read of <typeparamref name="T"/> is evicted. Also the same for every type
    /// the model lets the database delete or set to null when <typeparamref name="T"/> rows are deleted
    /// (<c>Cascade</c> or <c>SetNull</c>, transitively), so it covers a delete in raw SQL.
    /// </summary>
    /// <param name="set">The set that changed.</param>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> isn't mapped to a table.</exception>
    public static IEnumerable<Dependency> AllDependencies<T>(this DbSet<T> set)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(set);

        ModelSources.EnsureTable(set.EntityType);
        ModelSources.NameProvider(set);
        return ModelSources.AllDependencies(set.EntityType);
    }
}
