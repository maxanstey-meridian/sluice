using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// A compute's own DI scope, and the contexts <c>read.Db</c> took from it: one per context type, each marked
/// configured once the configure it was made with has run. The compute holds it, so the scope, and every context the
/// scope made, is disposed when the compute ends (SLUICE-CORE-015).
/// </summary>
/// <remarks>
/// The scope makes the compute's contexts as a request's scope makes a request's: through the app's own registration,
/// with any scoped services the context's constructor takes. Each context is the scope's to dispose, so a pooled one
/// goes back to its pool through EF's own lease.
/// </remarks>
internal sealed class ComputeContexts(AsyncServiceScope scope) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly List<(Type Type, DbContext Context, bool Configured)> _contexts = [];

    /// <summary>The compute's contexts, with its scope opened on first use.</summary>
    internal static ComputeContexts Of(SluiceReads read) =>
        ActiveCompute.Hold(read, services => new ComputeContexts(services.CreateAsyncScope()));

    /// <summary>
    /// Whether <paramref name="context"/> is one <c>read.Db</c> made for <paramref name="reads"/>' compute (compared by
    /// reference): null if not, else whether it was configured. Opens nothing.
    /// </summary>
    internal static bool? StateOf(SluiceReads reads, DbContext context) =>
        ActiveCompute.Held<ComputeContexts>(reads)?.StateOf(context);

    /// <summary>
    /// The compute's <typeparamref name="TContext"/>, taken from its scope on first use and configured there, before
    /// anything else can use it, by <paramref name="configure"/>. Only that first call may configure it.
    /// </summary>
    internal TContext Get<TContext>(SluiceReads read, Action<TContext>? configure)
        where TContext : DbContext
    {
        // Held while configure runs, so a task beside it can't get the context half-configured.
        lock (_lock)
        {
            foreach (var (type, existing, _) in _contexts)
            {
                if (type == typeof(TContext))
                {
                    return configure is null
                        ? (TContext)existing
                        : throw ActiveCompute.Violate(read, ConfiguredLate(typeof(TContext).Name));
                }
            }

            var context = Resolve<TContext>();
            TrackedReads.EnsureOn(context);
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            var index = _contexts.Count;
            _contexts.Add((typeof(TContext), context, false));
            if (configure is not null)
            {
                // A configure that fails may have left the context half set up (one tenant's filter, another's
                // database), so the compute caches nothing, even if it catches the exception.
                try
                {
                    configure(context);
                }
                catch (Exception failure)
                {
                    ActiveCompute.Violate(read, failure);
                    throw;
                }

                _contexts[index] = (typeof(TContext), context, true);
            }

            return context;
        }
    }

    private bool? StateOf(DbContext context)
    {
        lock (_lock)
        {
            foreach (var (_, held, configured) in _contexts)
            {
                if (ReferenceEquals(held, context))
                {
                    return configured;
                }
            }

            return null;
        }
    }

    private TContext Resolve<TContext>()
        where TContext : DbContext
    {
        var name = typeof(TContext).Name;
        object? resolved;
        try
        {
            resolved = scope.ServiceProvider.GetService(typeof(TContext));
        }
        catch (InvalidOperationException failure)
        {
            throw new InvalidOperationException(
                $"read.Db<{name}>() couldn't make a {name} in the compute's own DI scope: {failure.Message} If "
                    + $"{name}'s constructor takes scoped services, register it with AddDbContext<{name}>(...) or "
                    + $"AddDbContextFactory<{name}>(..., ServiceLifetime.Scoped): the default singleton factory makes "
                    + "it from the root provider, outside any scope.",
                failure
            );
        }

        return resolved as TContext
            ?? throw new InvalidOperationException(
                $"read.Db<{name}>() takes a {name} from the compute's own DI scope, and none is registered. Register "
                    + $"it with services.AddDbContext<{name}>(...), AddDbContextFactory<{name}>(...) or a pool, "
                    + "configured with UseSluice."
            );
    }

    private static InvalidOperationException ConfiguredLate(string name) =>
        new(
            $"read.Db<{name}>(configure) was called after the compute already had its {name}, which may have run "
                + $"queries without that configuration. Configure it on the compute's first read.Db<{name}> call, "
                + $"and call read.Db<{name}>() after that."
        );

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
