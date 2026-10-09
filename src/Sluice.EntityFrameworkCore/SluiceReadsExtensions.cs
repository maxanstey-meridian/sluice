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
/// <para>
/// The queries never track, so the cache never stores the context's live instances or its unsaved edits.
/// Cache projections or detached entities.
/// </para>
/// <para>
/// Reads are strict, so a compute can't read a row its dependencies don't cover. Inside a compute, every EF command
/// must come from a read helper: a plain query, lazy or explicit loading, <c>FromSql</c>, a compiled query or a write
/// throws <see cref="InvalidOperationException"/> when it runs, and so does a helper's query that runs inside a
/// transaction. To run any other EF query, run it in the fetch of <c>read.From(dependency, fetch)</c> with a declared
/// <see cref="Source{TKey}"/>: the fetch is a declared read, so its other queries aren't checked until its task
/// completes, though a helper's query in it still is. Reads that run no SQL aren't checked either: <c>Find</c> of an
/// entity the context already tracks, <c>Local</c>, and navigations already loaded hand back what the context holds,
/// edits included, so don't use them inside a compute.
/// </para>
/// <para>
/// A helper's query may be filtered, ordered, paged and projected. One that reads anything beyond its own rows throws
/// when EF compiles it, which happens when it runs, inside a compute or not: a join, a subquery or set operation over
/// another set or a <c>SqlQuery</c>, a navigation (<c>Include</c>, <c>o.Customer.Name</c>, <c>c.Orders.Count</c>,
/// many-to-many navigations, through a cast or an interface too), a database function you mapped, or a query filter
/// that does any of those or reads the context's state (a tenant filter); <c>IgnoreQueryFilters()</c> leaves the
/// filters out. So does one that calls <c>AsTracking()</c>, <c>ExecuteUpdate</c> or <c>ExecuteDelete</c>, or that
/// returns entities that load lazily or hold their context (or own a value that does). Owned types and JSON columns
/// are part of their owner's row, so they're fine, and auto-included navigations aren't loaded. Read related rows
/// with their own helper call. The call that ran the compute throws the violation, and caches nothing, even if the
/// compute catches it.
/// </para>
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
    /// <typeparamref name="T"/> isn't mapped to a table (keyless, a view, a function or a SQL query), its primary key is
    /// composite, or the context's provider isn't relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>. Inside a compute, the query throws it when it runs inside
    /// a transaction.
    /// </exception>
    public static IQueryable<T> Entity<T>(this SluiceReads read, DbSet<T> set, object key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);

        var source = ModelSources.Entity(set.EntityType);
        var property = ModelSources.KeyProperty(set.EntityType);
        ModelSources.EnsureKeyType(property, key, nameof(key));
        var query = StrictReads.Query(set);

        read.From(source.For(key));
        return WhereEquals(query, property, key);
    }

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> rows whose foreign key is <paramref name="principalKey"/>,
    /// and returns a no-tracking query for them. Any change to one of those rows evicts the reader, as does a row
    /// joining or leaving the set.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read.</param>
    /// <param name="foreignKey">
    /// The FK property (<c>o =&gt; o.CustomerId</c>), a shadow FK named by a constant
    /// (<c>o =&gt; EF.Property&lt;int&gt;(o, "CustomerId")</c>) or a reference navigation to the principal
    /// (<c>o =&gt; o.Customer</c>).
    /// </param>
    /// <param name="principalKey">The foreign key value, of the FK property's CLR type (not nullable).</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="foreignKey"/> doesn't name a single-column foreign key, names it through <c>EF.Property</c> with
    /// a name that isn't a constant, or <paramref name="principalKey"/> isn't of its CLR type.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table, or the context's provider isn't relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>. Inside a compute, the query throws it when it runs inside
    /// a transaction.
    /// </exception>
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
        var query = StrictReads.Query(set);

        read.From(ModelSources.ForeignKey(resolved).For(principalKey));
        return WhereEquals(query, property, principalKey);
    }

    /// <summary>
    /// Records a dependency on every <typeparamref name="T"/> row, and returns a no-tracking query over the set.
    /// Any write to <typeparamref name="T"/> evicts the reader.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read.</param>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table, or the context's provider isn't relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>. Inside a compute, the query throws it when it runs inside
    /// a transaction.
    /// </exception>
    public static IQueryable<T> All<T>(this SluiceReads read, DbSet<T> set)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);

        var source = ModelSources.Entity(set.EntityType);
        var query = StrictReads.Query(set);

        read.From(source.All);
        return query;
    }

    // e => Equals(EF.Property<TKey>(e, name), (TKey)key), with the key captured so EF sends it as a parameter.
    // object.Equals, as EF's own key lookups use, because a converted key type needn't define ==.
    private static IQueryable<T> WhereEquals<T>(IQueryable<T> query, IProperty property, object key)
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

        return query.Where(Expression.Lambda<Func<T, bool>>(equals, entity));
    }
}
