using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>Connects a <see cref="DbContext"/>'s reads and tracked writes to Sluice.</summary>
public static class SluiceDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Invalidates what each <c>SaveChanges</c> wrote once it has committed: immediately without a transaction, when
    /// an EF transaction commits or rolls back, or when an enlisted or ambient (<c>TransactionScope</c>) transaction
    /// completes. A save that fails or is cancelled still invalidates, since an earlier batch may have committed.
    /// Writes the change tracker doesn't see (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL) need
    /// <see cref="DbSetDependencyExtensions"/> and
    /// <see cref="SluiceCache.InvalidateAsync(IEnumerable{Dependency})"/>. Also makes reads strict: inside a compute,
    /// the context runs only queries built by the <see cref="SluiceReadsExtensions"/> helpers, and a helper's query
    /// reads only its own rows. The helpers refuse a context without it.
    /// </summary>
    /// <param name="builder">The context's options.</param>
    /// <param name="services">
    /// The application's services, holding <see cref="SluiceCache"/> (<c>AddSluice</c>) and logging: a failure to
    /// invalidate after a commit is logged rather than thrown.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Strict reads, inside a compute: a command not built by a read helper throws
    /// <see cref="InvalidOperationException"/> when it runs, so lazy loading, explicit loading, <c>FromSql</c> and
    /// compiled queries can't be used there outside the fetch of <c>read.From(dependency, fetch)</c>, writes can't be
    /// used there at all, and no read can run inside a transaction. Inside a compute or not, a helper's query that reads beyond its
    /// own rows (<see cref="SluiceReadsExtensions"/> lists how) throws when EF compiles it, which happens when it runs.
    /// Commands run outside a compute aren't checked.
    /// </para>
    /// <para>
    /// The query check is an EF singleton interceptor, shared by every Sluice context, so a context that also calls
    /// <c>UseInternalServiceProvider</c> can't use Sluice: EF refuses singleton interceptors from
    /// <c>AddInterceptors</c> there. It sees each query as the query expression interceptors added before it leave
    /// it: one of your own added after <c>UseSluice</c>, or in the context's <c>OnConfiguring</c>, rewrites the query
    /// after the check, so what it adds isn't checked.
    /// </para>
    /// <para>
    /// Relational providers only: Sluice reads each save's transaction from the context's relational connection. A
    /// context on any other provider throws <see cref="NotSupportedException"/> when it saves, before it writes, and
    /// the read helpers refuse it.
    /// </para>
    /// <para>
    /// Call <c>UseSluice</c> after adding any interceptor that changes entities in <c>SavingChanges</c> (soft delete,
    /// auditing, an outbox). Interceptors run in the order they were added, and Sluice records what a save writes in
    /// its own <c>SavingChanges</c>, so it misses changes made by an interceptor that runs after it. Interceptors added
    /// in the context's <c>OnConfiguring</c> are added after those in the <c>AddDbContext</c> options, so they always
    /// run after Sluice: add such interceptors where you call <c>UseSluice</c>, before it.
    /// </para>
    /// <para>
    /// Every context using the same <see cref="SluiceCache"/> shares one interceptor, so pooled contexts, context
    /// factories and contexts sharing a transaction need nothing more. When contexts share a transaction, the one
    /// that commits or rolls it back must use <c>UseSluice</c> with the same cache too, since only it sees the
    /// commit. A raw <c>DbTransaction</c> committed outside EF isn't seen, so its writes aren't invalidated. A
    /// promoted or distributed transaction can complete after <c>TransactionScope.Dispose</c> returns, and its writes
    /// are invalidated then.
    /// </para>
    /// <para>
    /// A row saved without being loaded first (attached, updated or removed from an object the context didn't
    /// query) has no original values, so Sluice can't tell which parent it had. When it can see that, because
    /// <c>Update()</c> marked every value changed, or the foreign key is still null or the default of its type (such as
    /// 0), it invalidates every <c>Children</c> reader of that foreign key. When it can't, because the object was
    /// created with another foreign key value, or with a nullable one left null, the old parent's <c>Children</c>
    /// readers aren't invalidated. So load the row before changing it, or invalidate
    /// <see cref="DbSetDependencyExtensions.AllDependencies{T}"/> after the save.
    /// </para>
    /// <para>
    /// Loading isn't enough on its own: don't call <c>Update()</c> or set the state to <c>Modified</c> on a row you
    /// loaded, as generic repositories often do. That looks the same as an update of an unloaded row, so each such
    /// save invalidates every <c>Children</c> reader of the row's foreign keys, for every parent. Let the change
    /// tracker find the changes instead.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="services"/> has no <see cref="SluiceCache"/> or no logging.
    /// </exception>
    public static DbContextOptionsBuilder UseSluice(
        this DbContextOptionsBuilder builder,
        IServiceProvider services
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);

        return builder.AddInterceptors(SluiceInterceptor.For(services), StrictReads.Instance);
    }

    /// <inheritdoc cref="UseSluice(DbContextOptionsBuilder, IServiceProvider)"/>
    /// <typeparam name="TContext">The context the options are for.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseSluice<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        IServiceProvider services
    )
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseSluice((DbContextOptionsBuilder)builder, services);
}
