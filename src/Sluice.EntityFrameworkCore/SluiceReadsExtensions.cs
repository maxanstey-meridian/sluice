using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>
/// EF Core reads inside a compute: the compute's own context, and helpers that narrow what a query depends on.
/// </summary>
/// <remarks>
/// Every EF query on <see cref="Db{TContext}(SluiceReads)"/>'s context records each entity type it reads, so any
/// write to one of them evicts the value. <see cref="Entity{T}(SluiceReads, DbSet{T}, object)"/>, its unique-column
/// overload and <see cref="Children"/> narrow that for their own rows: only a write to that row, or to the rows under
/// that parent, evicts it. Inside a compute, EF commands on any
/// other context throw, unless they run in the fetch of <c>read.From(dependency, fetch)</c>, and so do writes, reads
/// inside a transaction, and reads Sluice can't track: raw SQL, database functions, views and keyless types, query
/// filters that read shared state (a static property, the clock) or, unless the compute configured its context with
/// <c>read.Db(configure)</c>, the context's own (a settable field or auto-property, or a property marked
/// <see cref="SluiceContextStateAttribute"/>), and results holding entities that load lazily or hold their context.
/// A violation inside a compute also makes the call that ran it throw and cache nothing, even if the compute catches
/// it. The rules in full:
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
    /// The compute's own <typeparamref name="TContext"/>, taken on first use from a DI scope the compute opens for
    /// itself, as a request's scope makes one, and disposed with that scope when the compute ends. It doesn't track
    /// queries by default, holds nothing from outside the compute, and isn't in any transaction, so a query on it reads
    /// what's committed. Every query on it records the entity types it reads.
    /// </summary>
    /// <remarks>
    /// A query filter that reads the context's own state (<c>HasQueryFilter(t =&gt; t.TenantId == TenantId)</c>) throws
    /// on this context, since that state isn't in the cache key: set it with
    /// <see cref="Db{TContext}(SluiceReads, Action{TContext})"/> instead.
    /// </remarks>
    /// <typeparam name="TContext">
    /// The context type, registered as a scoped service (as <c>AddDbContext</c>, <c>AddDbContextFactory</c> and the
    /// pools all do) and configured with <c>UseSluice</c>.
    /// </typeparam>
    /// <param name="read">The compute's reads.</param>
    /// <returns>The same context for every call in one compute.</returns>
    /// <exception cref="InvalidOperationException">
    /// No <typeparamref name="TContext"/> is registered or it can't be made in the scope, the context isn't configured
    /// with <c>UseSluice</c>, the compute has already returned, or <paramref name="read"/> is another compute's.
    /// </exception>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    public static TContext Db<TContext>(this SluiceReads read)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(read);
        return ComputeContexts.Of(read).Get<TContext>(read, null);
    }

    /// <summary>
    /// The compute's own <typeparamref name="TContext"/>, as <see cref="Db{TContext}(SluiceReads)"/> gives it, set up
    /// by <paramref name="configure"/> once, when it's made, before anything can query it: the tenant its query
    /// filters read, or the connection string of the tenant's database. Later calls in the compute take no configure
    /// and return the same context.
    /// </summary>
    /// <remarks>
    /// Query filters that read a field or auto-property of the context holding a plain value
    /// (<c>HasQueryFilter(t =&gt; t.TenantId == TenantId)</c>) run on a configured context, where
    /// <see cref="Db{TContext}(SluiceReads)"/>'s throw. Sluice takes on trust that <paramref name="configure"/> sets
    /// every such value, and from the compute's arguments, which the cache key holds: the cached value is served to
    /// every caller with that key. A value the context's constructor took from a scoped service (the current request's
    /// tenant) is the first caller's, since the compute runs on that caller's flow; overwrite it here. Filters reading
    /// the state through a method, an unmarked property with a body or an object the context holds (a tenant service)
    /// still throw. To set a scoped service a filter reads through a property marked
    /// <see cref="SluiceContextStateAttribute"/>, use <see cref="Db{TContext}(SluiceReads, Action{TContext, IServiceProvider})"/>.
    /// </remarks>
    /// <typeparam name="TContext">
    /// The context type, registered and configured as for <see cref="Db{TContext}(SluiceReads)"/>.
    /// </typeparam>
    /// <param name="read">The compute's reads.</param>
    /// <param name="configure">Sets the context up from the compute's arguments.</param>
    /// <returns>The configured context.</returns>
    /// <exception cref="InvalidOperationException">
    /// The compute already has its <typeparamref name="TContext"/>, which may have been queried without the
    /// configuration; or as for <see cref="Db{TContext}(SluiceReads)"/>. This, and an exception
    /// <paramref name="configure"/> throws, also make the call that ran the compute throw and cache nothing.
    /// </exception>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    public static TContext Db<TContext>(this SluiceReads read, Action<TContext> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(configure);
        return ComputeContexts.Of(read).Get<TContext>(read, (db, _) => configure(db));
    }

    /// <summary>
    /// The compute's own <typeparamref name="TContext"/>, set up by <paramref name="configure"/> as
    /// <see cref="Db{TContext}(SluiceReads, Action{TContext})"/> does, with the compute's own DI scope beside it, so
    /// the configure can set a scoped service the context reads, such as the tenant
    /// (<c>(db, services) =&gt; services.GetRequiredService&lt;TenantProvider&gt;().Set(tenantId)</c>). On a context
    /// <c>AddDbContext</c> or a scoped factory makes, that's the scope the context was made in, so the service is the one
    /// it holds; a context leased straight from <c>AddDbContextPool</c> keeps whatever an earlier lease gave it.
    /// </summary>
    /// <remarks>
    /// A query filter may read such a service through a property of the context marked
    /// <see cref="SluiceContextStateAttribute"/>, whose body Sluice takes on trust to read only what this configure
    /// sets. Unmarked properties with a body, methods, and members of objects the context holds are still refused.
    /// </remarks>
    /// <typeparam name="TContext">
    /// The context type, registered and configured as for <see cref="Db{TContext}(SluiceReads)"/>.
    /// </typeparam>
    /// <param name="read">The compute's reads.</param>
    /// <param name="configure">
    /// Sets the context, and the scoped services it reads, up from the compute's arguments. Its second argument is
    /// the compute's own scope, disposed when the compute ends.
    /// </param>
    /// <returns>The configured context.</returns>
    /// <exception cref="InvalidOperationException">
    /// As for <see cref="Db{TContext}(SluiceReads, Action{TContext})"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    public static TContext Db<TContext>(
        this SluiceReads read,
        Action<TContext, IServiceProvider> configure
    )
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(configure);
        return ComputeContexts.Of(read).Get(read, configure);
    }

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> row with primary key <paramref name="key"/>, and returns
    /// a no-tracking query for it. Only a write to that row evicts the reader, where a plain query would be evicted by
    /// a write to any <typeparamref name="T"/>. A read that finds nothing still depends on the key, so creating the
    /// row evicts it. The query may also read other types, through navigations or <c>Include</c>; each of those is
    /// recorded whole. It doesn't load auto-included navigations anywhere in the query, since EF applies that to the
    /// whole query: what you <c>Include</c> loses its own auto-included navigations, as does a plain query joined or
    /// combined with it. <c>Include</c> each one you need explicitly
    /// (<c>.Include(t =&gt; t.Comments).ThenInclude(c =&gt; c.Author)</c>).
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read, on the context <see cref="Db{TContext}(SluiceReads)"/> returns.</param>
    /// <param name="key">The primary key, of its CLR type in the EF model.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't of the primary key's CLR type.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> isn't mapped to a table (keyless, a view, a function or a SQL query), its primary key is
    /// composite, or the context's provider isn't relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>, or a compute is running and <paramref name="read"/> isn't
    /// the one it was given or the context isn't the one <see cref="Db{TContext}(SluiceReads)"/> returned.
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
    /// Records a dependency on the <typeparamref name="T"/> row whose <paramref name="uniqueColumn"/> is
    /// <paramref name="value"/>, and returns a no-tracking query for it: a lookup by slug or email that a write to
    /// another row doesn't evict. Any write that gives a row that value, changes the row holding it, or takes the
    /// value from it, evicts the reader, so a read that finds nothing is evicted once a row gets the value. The rules
    /// on navigations, <c>Include</c> and auto-included navigations are those of
    /// <see cref="Entity{T}(SluiceReads, DbSet{T}, object)"/>.
    /// </summary>
    /// <remarks>
    /// Without a row version (on PostgreSQL, <c>xmin</c>), or the column as a concurrency token, on a row stored in one
    /// table, a save that updates or deletes a <typeparamref name="T"/> can't know the value the database holds, so it
    /// evicts every such reader of <typeparamref name="T"/>, as it does <see cref="Children"/> lists.
    /// </remarks>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read, on the context <see cref="Db{TContext}(SluiceReads)"/> returns.</param>
    /// <param name="uniqueColumn">
    /// The column (<c>f =&gt; f.Slug</c>), or a shadow one named by a constant
    /// (<c>f =&gt; EF.Property&lt;string&gt;(f, "Slug")</c>), that a single-column unique index (filtered or not) or
    /// alternate key covers in the EF model. The primary key reads as <see cref="Entity{T}(SluiceReads, DbSet{T}, object)"/>.
    /// </param>
    /// <param name="value">The value, of the column's CLR type in the EF model; not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="uniqueColumn"/> doesn't name a property, names it through <c>EF.Property</c> with a name that
    /// isn't a constant, or names one no single-column unique index or alternate key covers; or
    /// <paramref name="value"/> isn't of its CLR type, or its value converter stores it as null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Only a composite unique index or key covers the column, its value converter stores null as a value
    /// (<c>convertsNulls</c>), <typeparamref name="T"/> isn't mapped to a table, or the context's provider isn't
    /// relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>, or a compute is running and <paramref name="read"/> isn't
    /// the one it was given or the context isn't the one <see cref="Db{TContext}(SluiceReads)"/> returned.
    /// </exception>
    public static IQueryable<T> Entity<T>(
        this SluiceReads read,
        DbSet<T> set,
        Expression<Func<T, object?>> uniqueColumn,
        object value
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(uniqueColumn);
        ArgumentNullException.ThrowIfNull(value);

        var entityType = set.EntityType;
        ModelSources.EnsureTable(entityType);
        var property = ModelSources.ResolveUniqueColumn(
            entityType,
            uniqueColumn,
            nameof(uniqueColumn)
        );
        ModelSources.EnsureKeyType(property, value, nameof(value), "value");
        ModelSources.EnsureStored(property, value, nameof(value));
        var query = TrackedReads.Query(set);

        read.From(
            property.IsPrimaryKey()
                ? ModelSources.Entity(entityType).For(value)
                : ModelSources.Unique(property).For(value)
        );
        return WhereEquals(query, property, value);
    }

    /// <summary>
    /// Records a dependency on the <typeparamref name="T"/> rows whose foreign key is <paramref name="principalKey"/>,
    /// and returns a no-tracking query for them. Any change to one of those rows evicts the reader, as does a row
    /// joining or leaving the set; a write to other <typeparamref name="T"/> rows doesn't. The query may also read
    /// other types, through navigations or <c>Include</c>; each of those is recorded whole. It doesn't load
    /// auto-included navigations anywhere in the query, since EF applies that to the whole query: what you
    /// <c>Include</c> loses its own auto-included navigations, as does a plain query joined or combined with it.
    /// <c>Include</c> each one you need explicitly (<c>.Include(o =&gt; o.Lines).ThenInclude(l =&gt; l.Product)</c>).
    /// </summary>
    /// <param name="read">The compute's reads.</param>
    /// <param name="set">The set to read, on the context <see cref="Db{TContext}(SluiceReads)"/> returns.</param>
    /// <param name="foreignKey">
    /// The FK property (<c>o =&gt; o.CustomerId</c>), a shadow FK named by a constant
    /// (<c>o =&gt; EF.Property&lt;CustomerId&gt;(o, "CustomerId")</c>) or a reference navigation to the principal
    /// (<c>o =&gt; o.Customer</c>).
    /// </param>
    /// <param name="principalKey">
    /// The foreign key value, of the FK property's CLR type. The property may be nullable, but the key can't be null:
    /// rows with no parent aren't anyone's children.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="principalKey"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="foreignKey"/> doesn't name a single-column foreign key, names it through <c>EF.Property</c> with
    /// a name that isn't a constant, or <paramref name="principalKey"/> isn't of its CLR type, or its value converter
    /// stores it as null.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The foreign key's value converter stores null as a value (<c>convertsNulls</c>), <typeparamref name="T"/> isn't
    /// mapped to a table, or the context's provider isn't relational.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The context isn't configured with <c>UseSluice</c>, or a compute is running and <paramref name="read"/> isn't
    /// the one it was given or the context isn't the one <see cref="Db{TContext}(SluiceReads)"/> returned.
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
        ModelSources.EnsureStored(property, principalKey, nameof(principalKey));
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
