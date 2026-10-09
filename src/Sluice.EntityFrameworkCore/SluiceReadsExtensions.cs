using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>
/// EF Core reads that record their dependency from the EF model and return a no-tracking query to compose.
/// </summary>
/// <remarks>
/// The queries never track, so the cache never stores the context's live instances or its unsaved edits.
/// Cache projections or detached entities.
/// </remarks>
public static class SluiceReadsExtensions
{
    private static readonly MethodInfo _efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo _objectEquals = typeof(object).GetMethod(
        nameof(Equals),
        [typeof(object), typeof(object)]
    )!;

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> row with primary key <paramref name="key"/>, and returns
    /// a no-tracking query for it. A read that finds nothing still depends on the key, so creating the row evicts it.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read.</param>
    /// <param name="key">The primary key, of its CLR type in the EF model.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't of the primary key's CLR type.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table (keyless, a view, a function or a SQL query), or its primary key is composite.
    /// </exception>
    /// <exception cref="InvalidOperationException">The context is in a transaction.</exception>
    public static IQueryable<T> Entity<T>(this SluiceReads read, DbSet<T> set, object key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);

        var source = ModelSources.Entity(set.EntityType);
        var property = ModelSources.KeyProperty(set.EntityType);
        ModelSources.EnsureKeyType(property, key, nameof(key));
        EnsureNoTransaction(set);

        read.From(source.For(key));
        return WhereEquals(set, property, key);
    }

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> rows whose foreign key is <paramref name="principalKey"/>,
    /// and returns a no-tracking query for them. Any change to one of those rows evicts the reader, as does a row
    /// joining or leaving the set.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read.</param>
    /// <param name="foreignKey">
    /// The FK property (<c>o =&gt; o.CustomerId</c>), a shadow FK (<c>o =&gt; EF.Property&lt;int&gt;(o, "CustomerId")</c>)
    /// or a reference navigation to the principal (<c>o =&gt; o.Customer</c>).
    /// </param>
    /// <param name="principalKey">The foreign key value, of the FK property's CLR type (not nullable).</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="foreignKey"/> doesn't name a single-column foreign key, or <paramref name="principalKey"/>
    /// isn't of its CLR type.
    /// </exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> isn't mapped to a table.</exception>
    /// <exception cref="InvalidOperationException">The context is in a transaction.</exception>
    public static IQueryable<T> Children<T>(
        this SluiceReads read,
        DbSet<T> set,
        Expression<Func<T, object?>> foreignKey,
        object principalKey
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(foreignKey);
        ArgumentNullException.ThrowIfNull(principalKey);

        ModelSources.EnsureTable(set.EntityType);
        var resolved = ModelSources.ResolveForeignKey(
            set.EntityType,
            foreignKey,
            nameof(foreignKey)
        );
        var property = resolved.Properties[0];
        ModelSources.EnsureKeyType(property, principalKey, nameof(principalKey));
        EnsureNoTransaction(set);

        read.From(ModelSources.ForeignKey(resolved).For(principalKey));
        return WhereEquals(set, property, principalKey);
    }

    /// <summary>
    /// Records a dependency on every <typeparamref name="T"/> row, and returns a no-tracking query over the set.
    /// Any write to <typeparamref name="T"/> evicts the reader.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read.</param>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> isn't mapped to a table.</exception>
    /// <exception cref="InvalidOperationException">The context is in a transaction.</exception>
    public static IQueryable<T> All<T>(this SluiceReads read, DbSet<T> set)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);

        var source = ModelSources.Entity(set.EntityType);
        EnsureNoTransaction(set);

        read.From(source.All);
        return set.AsNoTracking();
    }

    // Inside a transaction a compute would cache uncommitted rows for every caller, or an old snapshot under
    // RepeatableRead or Serializable.
    private static void EnsureNoTransaction<T>(DbSet<T> set)
        where T : class
    {
        if (!ContextTransaction.Of(set).IsNone)
        {
            throw new InvalidOperationException(
                "Sluice's EF reads can't run inside a transaction: the compute would cache rows other callers "
                    + "can't see yet, or an old snapshot. Compute the value outside the transaction, or after it commits."
            );
        }
    }

    // e => Equals(EF.Property<TKey>(e, name), (TKey)key), with the key captured so EF sends it as a parameter.
    // object.Equals, as EF's own key lookups use, because a converted key type needn't define ==.
    private static IQueryable<T> WhereEquals<T>(DbSet<T> set, IProperty property, object key)
        where T : class
    {
        var entity = Expression.Parameter(typeof(T), "e");
        var column = Expression.Call(
            _efProperty.MakeGenericMethod(property.ClrType),
            entity,
            Expression.Constant(property.Name)
        );
        Expression<Func<object>> captured = () => key;
        var equals = Expression.Call(
            _objectEquals,
            Expression.Convert(column, typeof(object)),
            Expression.Convert(Expression.Convert(captured.Body, property.ClrType), typeof(object))
        );

        return set.AsNoTracking().Where(Expression.Lambda<Func<T, bool>>(equals, entity));
    }
}
