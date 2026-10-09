using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>Connects a <see cref="DbContext"/>'s tracked writes to Sluice.</summary>
public static class SluiceDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Invalidates what each <c>SaveChanges</c> wrote once it has committed: immediately without a transaction, when
    /// an EF transaction commits or rolls back, or when an enlisted or ambient (<c>TransactionScope</c>) transaction
    /// completes. A save that fails or is cancelled still invalidates, since an earlier batch may have committed.
    /// Writes the change tracker doesn't see (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL) need
    /// <see cref="DbSetDependencyExtensions"/> and
    /// <see cref="SluiceCache.InvalidateAsync(IEnumerable{Dependency})"/>.
    /// </summary>
    /// <param name="builder">The context's options.</param>
    /// <param name="services">
    /// The application's services, holding <see cref="SluiceCache"/> (<c>AddSluice</c>) and logging: a failure to
    /// invalidate after a commit is logged rather than thrown.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// Every context using the same <see cref="SluiceCache"/> shares one interceptor, so pooled contexts, context
    /// factories and contexts sharing a transaction need nothing more. A raw <c>DbTransaction</c> committed outside
    /// EF isn't seen, so its writes aren't invalidated. A promoted or distributed transaction can complete after
    /// <c>TransactionScope.Dispose</c> returns, and its writes are invalidated then.
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

        return builder.AddInterceptors(SluiceInterceptor.For(services));
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
