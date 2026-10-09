using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore;
using Sluice.Infrastructure;

namespace Sluice;

/// <summary>
/// EF Core reads inside a compute: the compute's own context, and helpers that narrow what a query depends on.
/// </summary>
/// <remarks>
/// Every EF query on <see cref="Db{TContext}"/>'s context records each entity type it reads, so any write to one of
/// them evicts the value. <see cref="Entity"/> and <see cref="Children"/> narrow that for their own rows: only a
/// write to that row, or to the rows under that parent, evicts it. Inside a compute, EF commands on any other context
/// throw, unless they run in the fetch of <c>read.From(dependency, fetch)</c>, and so do writes, reads inside a
/// transaction, and reads Sluice can't track: raw SQL, database functions, views and keyless types, query filters
/// that read the context's or other shared state (a static property, the clock), and results holding entities that
/// load lazily or hold their context. A violation inside a compute also makes the call that ran it throw and cache
/// nothing, even if the compute catches it. The rules in full:
/// <see href="https://maxanstey-meridian.github.io/sluice/guides/ef-core">EF Core</see>.
/// </remarks>
public static class SluiceReadsExtensions
{
    private static readonly MethodInfo _efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo _objectEquals = typeof(object).GetMethod(
        nameof(Equals),
        [typeof(object), typeof(object)]
    )!;

    /// <summary>
    /// The compute's own <typeparamref name="TContext"/>, made from the registered
    /// <see cref="IDbContextFactory{TContext}"/> on first use and disposed when the compute ends. It doesn't track
    /// queries by default, holds nothing from outside the compute, and isn't in any transaction, so a query on it reads
    /// what's committed. Every query on it records the entity types it reads.
    /// </summary>
    /// <typeparam name="TContext">The context type, configured with <c>UseSluice</c>.</typeparam>
    /// <param name="read">The compute's reads.</param>
    /// <returns>The same context for every call in one compute.</returns>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IDbContextFactory{TContext}"/> is registered, the context isn't configured with
    /// <c>UseSluice</c>, or the compute has already returned.
    /// </exception>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    public static TContext Db<TContext>(this SluiceReads read)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(read);
        return ActiveCompute.Hold(read, Create<TContext>);
    }

    private static TContext Create<TContext>(IServiceProvider services)
        where TContext : DbContext
    {
        var factory =
            services.GetService<IDbContextFactory<TContext>>()
            ?? throw new InvalidOperationException(
                $"read.Db<{typeof(TContext).Name}>() needs an IDbContextFactory<{typeof(TContext).Name}>. Register "
                    + $"the context with services.AddDbContextFactory<{typeof(TContext).Name}>(...) (or "
                    + "AddPooledDbContextFactory), configured with UseSluice; it registers the context itself too, "
                    + "for the rest of your code."
            );
        var context = factory.CreateDbContext();
        try
        {
            TrackedReads.EnsureOn(context);
        }
        catch
        {
            context.Dispose();
            throw;
        }

        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return context;
    }

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> row with primary key <paramref name="key"/>, and returns
    /// a no-tracking query for it. Only a write to that row evicts the reader, where a plain query would be evicted by
    /// a write to any <typeparamref name="T"/>. A read that finds nothing still depends on the key, so creating the
    /// row evicts it. The query may also read other types, through navigations or <c>Include</c>; each of those is
    /// recorded whole. It doesn't load auto-included navigations, and EF applies that to the whole query: a plain
    /// query joined or combined with it doesn't load its own auto-included navigations either.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read, on the context <see cref="Db{TContext}"/> returns.</param>
    /// <param name="key">The primary key, of its CLR type in the EF model.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't of the primary key's CLR type.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table (keyless, a view, a function or a SQL query), its primary key is
    /// composite, or the context's provider isn't relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>, or a compute is running and <paramref name="read"/> isn't
    /// the one it was given or the context isn't the one <see cref="Db{TContext}"/> returned.
    /// </exception>
    public static IQueryable<T> Entity<T>(this SluiceReads read, DbSet<T> set, object key)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);

        var dependency = ModelSources.EntityDependency(set.EntityType, key, nameof(key));
        var query = TrackedReads.Query(set);

        read.From(dependency);
        return WhereEquals(query, ModelSources.KeyProperty(set.EntityType), key);
    }

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> rows whose foreign key is <paramref name="principalKey"/>,
    /// and returns a no-tracking query for them. Any change to one of those rows evicts the reader, as does a row
    /// joining or leaving the set; a write to other <typeparamref name="T"/> rows doesn't. The query may also read
    /// other types, through navigations or <c>Include</c>; each of those is recorded whole. It doesn't load
    /// auto-included navigations, and EF applies that to the whole query: a plain query joined or combined with it
    /// doesn't load its own auto-included navigations either.
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read, on the context <see cref="Db{TContext}"/> returns.</param>
    /// <param name="foreignKey">
    /// The FK property (<c>o =&gt; o.CustomerId</c>), a shadow FK named by a constant
    /// (<c>o =&gt; EF.Property&lt;CustomerId&gt;(o, "CustomerId")</c>) or a reference navigation to the principal
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
    /// The context isn't configured with <c>UseSluice</c>, or a compute is running and <paramref name="read"/> isn't
    /// the one it was given or the context isn't the one <see cref="Db{TContext}"/> returned.
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
        var query = TrackedReads.Query(set);

        read.From(ModelSources.ForeignKey(resolved).For(principalKey));
        return WhereEquals(query, property, principalKey);
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
