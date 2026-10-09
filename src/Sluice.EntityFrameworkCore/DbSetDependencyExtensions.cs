using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>
/// The dependencies Sluice derives from the EF model, for invalidating after a write the change tracker doesn't
/// see: <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL, or another service. Pass them to
/// <see cref="SluiceCache.InvalidateAsync(IEnumerable{Dependency})"/> after the write commits; they batch with
/// dependencies from declared sources.
/// </summary>
public static class DbSetDependencyExtensions
{
    /// <summary>
    /// The dependency on the <typeparamref name="T"/> row with primary key <paramref name="key"/>. Invalidating it
    /// evicts <see cref="SluiceReadsExtensions.Entity"/> readers of that row and <see cref="SluiceReadsExtensions.All"/>
    /// readers of <typeparamref name="T"/>, but not <see cref="SluiceReadsExtensions.Children"/> readers of the
    /// row's parent: Sluice can't see the row's foreign keys. After a bypass that changed such rows, use
    /// <see cref="AllDependencies"/>.
    /// </summary>
    /// <param name="set">The set the row belongs to.</param>
    /// <param name="key">The primary key, of its CLR type in the EF model.</param>
    /// <returns>What <see cref="SluiceReadsExtensions.Entity"/> records for the same key.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't of the primary key's CLR type.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table, or its primary key is composite.
    /// </exception>
    public static Dependency Dependency<T>(this DbSet<T> set, object key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);

        return ModelSources.EntityDependency(set.EntityType, key, nameof(key));
    }

    /// <summary>
    /// Every dependency on <typeparamref name="T"/>: the whole set, and every foreign key it (or a derived type)
    /// declares, so <see cref="SluiceReadsExtensions.Entity"/>, <see cref="SluiceReadsExtensions.All"/> and
    /// <see cref="SluiceReadsExtensions.Children"/> readers of <typeparamref name="T"/> are all evicted. Also the
    /// same for every type the model lets the database delete or set to null when <typeparamref name="T"/> rows are
    /// deleted (<c>Cascade</c> or <c>SetNull</c>, transitively), so it covers an <c>ExecuteDelete</c>.
    /// </summary>
    /// <param name="set">The set that changed.</param>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> isn't mapped to a table.</exception>
    public static IEnumerable<Dependency> AllDependencies<T>(this DbSet<T> set)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(set);

        var entityType = set.EntityType;
        return
        [
            ModelSources.Entity(entityType).All,
            .. entityType
                .GetForeignKeys()
                .Concat(entityType.GetDerivedForeignKeys())
                .Concat(ModelSources.CascadingForeignKeys(entityType))
                .Select(foreignKey => ModelSources.ForeignKey(foreignKey).All),
            .. ModelSources.Cascades(entityType),
        ];
    }
}
