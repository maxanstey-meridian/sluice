using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>Connects a <see cref="DbContext"/>'s reads and tracked writes to Sluice.</summary>
public static class SluiceDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Invalidates what each <c>SaveChanges</c>, <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> wrote once its
    /// transaction has committed, and records what each query inside a compute reads. Calling it again for the same
    /// options does nothing.
    /// </summary>
    /// <param name="builder">The context's options.</param>
    /// <param name="services">
    /// The application's services, holding <see cref="SluiceCache"/> (<c>AddSluice</c>) and logging: a failure to
    /// invalidate after a commit is logged rather than thrown.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// Call it after any interceptor that changes entities in <c>SavingChanges</c>, since Sluice records what a save
    /// writes in its own. Relational providers only; a context using <c>UseInternalServiceProvider</c> can't use it.
    /// What a save invalidates, and when: <see href="https://maxanstey-meridian.github.io/sluice/guides/ef-core">EF
    /// Core</see> and <see href="https://maxanstey-meridian.github.io/sluice/guides/transactions">Transactions</see>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="services"/> has no <see cref="SluiceCache"/> or no logging, or resolving the cache fails its
    /// lifetime check.
    /// </exception>
    public static DbContextOptionsBuilder UseSluice(
        this DbContextOptionsBuilder builder,
        IServiceProvider services
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);

        // EF composes every configuration registered for a context type (AddDbContext and AddDbContextFactory for the
        // same type, say), so this can run twice for one context. Twice would capture each save twice, and the second
        // capture would publish the first before its commit.
        if (TrackedReads.IsOn(builder.Options))
        {
            return builder;
        }

        return builder.AddInterceptors(SluiceInterceptor.For(services), TrackedReads.Instance);
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
