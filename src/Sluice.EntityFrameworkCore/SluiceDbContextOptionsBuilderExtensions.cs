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
    /// The application's services, holding <see cref="SluiceCache"/> (<c>AddSluice</c> and
    /// <c>AddSluiceEntityFrameworkCore</c>) and logging: a failure to
    /// invalidate after a commit is logged rather than thrown.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// Sluice records what a save writes when EF initialises its first command, after every <c>SavingChanges</c>
    /// interceptor, so the order of save interceptors doesn't matter. Relational providers only; a context using
    /// <c>UseInternalServiceProvider</c> can't use it.
    /// What a save invalidates, and when: <see href="https://maxanstey-meridian.github.io/sluice/guides/ef-core">EF
    /// Core</see> and <see href="https://maxanstey-meridian.github.io/sluice/guides/transactions">Transactions</see>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="services"/> has no <see cref="SluiceCache"/> (<c>AddSluice</c>), wasn't given
    /// <c>AddSluiceEntityFrameworkCore</c>, or has no logging, or resolving the cache fails its checks.
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

        // The reads interceptor first: a command it refuses inside a compute (a save, say) throws before the write
        // interceptor captures it, so a write that never ran publishes nothing.
        return builder.AddInterceptors(TrackedReads.Instance, SluiceInterceptor.For(services));
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
