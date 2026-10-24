using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// A compute's own DI scope, and the contexts <c>read.Db</c> made in it: one per context type, each marked configured
/// once the configure it was made with has run. The compute holds it, so the scope, every context the scope made and
/// every context this built, is disposed when the compute ends (SLUICE-CORE-015).
/// </summary>
/// <remarks>
/// The scope makes the compute's contexts as a request's scope makes a request's: through the app's own registration,
/// with any scoped services the context's constructor takes. A pooled context is never leased: a lease keeps what the
/// compute's configure set into the next lease, a request's, and keeps the services an earlier lease gave it. It's
/// built from its pooled options instead, with its constructor's services from the compute's scope. Contexts, and
/// the services the configure asks for, are made without the caller's async state (<see cref="CleanFlow"/>), so they
/// keep nothing of the first caller's request.
/// </remarks>
internal sealed class ComputeContexts(IServiceProvider root, AsyncServiceScope scope)
    : IAsyncDisposable
{
    // Whether each context type comes out of two DI scopes as one instance, per application, worked out once.
    private static readonly ConditionalWeakTable<
        IServiceProvider,
        ConcurrentDictionary<Type, bool>
    > _sharedByScopes = new();

    // How to build each context type from its options, with its other constructor services from a scope.
    private static readonly ConcurrentDictionary<
        (Type Context, Type Options),
        ObjectFactory
    > _builders = new();

    // Taken to make a context, so a task beside the configure can't get it half-configured.
    private readonly Lock _lock = new();

    // Replaced whole under the lock, and read without it, so a command never waits on a configure.
    private volatile (Type Type, DbContext Context, bool Configured)[] _contexts = [];

    // The contexts this built from pooled options, which are this compute's to dispose, not the scope's. Under the lock.
    private readonly List<DbContext> _built = [];

    // The scopes SharedByScopes opened to compare with, disposed with the compute's. Under the lock.
    private readonly List<AsyncServiceScope> _probes = [];

    // The scope's services as the configure gets them. Made under the lock.
    private IServiceProvider? _configureServices;

    /// <summary>The compute's contexts, with its scope opened on first use.</summary>
    internal static ComputeContexts Of(SluiceReads read) =>
        ActiveCompute.Hold(
            read,
            services => new ComputeContexts(services, services.CreateAsyncScope())
        );

    /// <summary>
    /// Whether <paramref name="context"/> is one <c>read.Db</c> made for <paramref name="reads"/>' compute (compared by
    /// reference), and if so whether it was configured. Opens nothing.
    /// </summary>
    internal static ComputeContext StateOf(SluiceReads reads, DbContext context) =>
        ActiveCompute.Held<ComputeContexts>(reads)?.StateOf(context) ?? ComputeContext.Foreign;

    /// <summary>
    /// The compute's <typeparamref name="TContext"/>, made on first use and configured there, before anything else can
    /// use it, by <paramref name="configure"/>, which gets the scope's services too. Only that first call may
    /// configure it.
    /// </summary>
    internal TContext Get<TContext>(SluiceReads read, Action<TContext, IServiceProvider>? configure)
        where TContext : DbContext
    {
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

            var context = CleanFlow.Run(
                static state => state.Contexts.Make<TContext>(state.Read),
                (Contexts: this, Read: read)
            );
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            var index = _contexts.Length;
            _contexts = [.. _contexts, (typeof(TContext), context, false)];
            if (configure is not null)
            {
                // A configure that fails may have left the context half set up (one tenant's filter, another's
                // database), so the compute caches nothing, even if it catches the exception. The configure itself
                // runs on the compute's flow, so a query it runs is checked.
                try
                {
                    configure(
                        context,
                        _configureServices ??= CleanFlow.Services(scope.ServiceProvider)
                    );
                }
                catch (Exception failure)
                {
                    ActiveCompute.Violate(read, failure);
                    throw;
                }

                // The configure may have asked for other contexts, which come after this one.
                var configured = _contexts.ToArray();
                configured[index] = (typeof(TContext), context, true);
                _contexts = configured;
            }

            return context;
        }
    }

    private ComputeContext StateOf(DbContext context)
    {
        foreach (var (_, held, configured) in _contexts)
        {
            if (ReferenceEquals(held, context))
            {
                return configured ? ComputeContext.Configured : ComputeContext.Unconfigured;
            }
        }

        return ComputeContext.Foreign;
    }

    // A pooled registration's options, which carry the pool's size, give a context built from them rather than a
    // lease. A lease the registration hands out anyway (a pool the app built by hand) is left to the scope, which
    // returns it untouched, and a context is built from its options instead. Any other context is the scope's own,
    // unless every scope gets the same one. EF runs OnConfiguring when a context is first used, which reading its
    // options does, so that runs here too, without the caller's async state.
    private TContext Make<TContext>(SluiceReads read)
        where TContext : DbContext
    {
        if (
            scope.ServiceProvider.GetService<DbContextOptions<TContext>>() is { } options
            && IsPooled(options)
        )
        {
            return Checked((TContext)Build(typeof(TContext), options));
        }

        var resolved = Resolve<TContext>();
        var verdicts = _sharedByScopes.GetValue(root, static _ => new());
        if (verdicts.TryGetValue(typeof(TContext), out var shared) && shared)
        {
            throw ActiveCompute.Violate(read, Shared(typeof(TContext).Name));
        }

        if (resolved.GetService<IDbContextOptions>() is DbContextOptions own && IsPooled(own))
        {
            return Checked((TContext)Build(resolved.GetType(), own));
        }

        Checked(resolved);
        if (SharedByScopes(resolved, verdicts))
        {
            throw ActiveCompute.Violate(read, Shared(typeof(TContext).Name));
        }

        return resolved;
    }

    private static TContext Checked<TContext>(TContext context)
        where TContext : DbContext
    {
        TrackedReads.EnsureOn(context);
        return context;
    }

    private static bool IsPooled(IDbContextOptions options) =>
        options.FindExtension<CoreOptionsExtension>()?.MaxPoolSize is not null;

    private DbContext Build(Type type, DbContextOptions options)
    {
        var builder = _builders.GetOrAdd(
            (type, options.GetType()),
            static types => ActivatorUtilities.CreateFactory(types.Context, [types.Options])
        );
        var context = (DbContext)builder(scope.ServiceProvider, [options]);
        _built.Add(context);
        return context;
    }

    private TContext Resolve<TContext>()
        where TContext : DbContext
    {
        var name = typeof(TContext).Name;
        TContext? resolved;
        try
        {
            resolved = scope.ServiceProvider.GetService<TContext>();
        }
        catch (InvalidOperationException failure) when (failure is not ObjectDisposedException)
        {
            throw new InvalidOperationException(
                $"read.Db<{name}>() couldn't make a {name} in the compute's own DI scope: {failure.Message} If "
                    + $"{name}'s constructor takes scoped services, register it with AddDbContext<{name}>(...) or "
                    + $"AddDbContextFactory<{name}>(..., ServiceLifetime.Scoped): the default singleton factory makes "
                    + "it from the root provider, outside any scope.",
                failure
            );
        }

        return resolved
            ?? throw new InvalidOperationException(
                $"read.Db<{name}>() takes a {name} from the compute's own DI scope, and none is registered. Register "
                    + $"it with services.AddDbContext<{name}>(...), AddDbContextFactory<{name}>(...) or a pool, "
                    + "configured with UseSluice."
            );
    }

    // Whether another scope of the app's gets this very instance: a singleton registration, or a factory that hands
    // out one instance. Worked out once per application and type, from a scope of the app's own, and never kept when
    // the probe fails, which refuses nothing. A shared context is refused from the verdict alone next time, since
    // the scope that made it may have disposed it.
    private bool SharedByScopes<TContext>(
        TContext context,
        ConcurrentDictionary<Type, bool> verdicts
    )
        where TContext : DbContext
    {
        if (verdicts.TryGetValue(typeof(TContext), out var known))
        {
            return known;
        }

        bool shared;
        try
        {
            // Disposed with the compute's own scope, not here, under the lock.
            var probe = root.CreateAsyncScope();
            _probes.Add(probe);
            shared = ReferenceEquals(probe.ServiceProvider.GetService<TContext>(), context);
        }
        catch (Exception)
        {
            return false;
        }

        verdicts[typeof(TContext)] = shared;
        return shared;
    }

    private static InvalidOperationException Shared(string name) =>
        new(
            $"read.Db<{name}>() got the same {name} from two DI scopes, so it's registered as a singleton or its "
                + "registration hands out one instance. Every compute and request would share it, and a compute's "
                + "configure would change it under them. Register it scoped (AddDbContext's default) or transient, or "
                + $"with AddDbContextFactory<{name}>(…, ServiceLifetime.Scoped)."
        );

    private static InvalidOperationException ConfiguredLate(string name) =>
        new(
            $"read.Db<{name}>(configure) was called after an earlier call had already made the compute's {name} (and "
                + "maybe configured it and run queries on it). Configure it on the compute's first "
                + $"read.Db<{name}> call, and call read.Db<{name}>() after that."
        );

    // What this built goes first: the scope may hold services those contexts use. Each is disposed even when one
    // before it throws, as the compute disposes what it holds (SLUICE-CORE-015), and the caller gets what they threw.
    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;
        IAsyncDisposable[] disposables = [.. _built, .. _probes, scope];
        foreach (var disposable in disposables)
        {
            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        switch (failures)
        {
            case [var only]:
                ExceptionDispatchInfo.Throw(only);
                break;
            case { Count: > 1 }:
                throw new AggregateException(
                    "Disposing the compute's EF contexts and DI scope threw more than once.",
                    failures
                );
        }
    }
}

/// <summary>Whether a context is one <c>read.Db</c> made for the running compute, and whether its configure ran.</summary>
internal enum ComputeContext
{
    /// <summary>Not made by <c>read.Db</c> for this compute.</summary>
    Foreign,

    /// <summary>Made for this compute without a configure.</summary>
    Unconfigured,

    /// <summary>Made for this compute, and its configure has returned.</summary>
    Configured,
}
