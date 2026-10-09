using System.Runtime.ExceptionServices;
using Sluice.Infrastructure;

namespace Sluice;

/// <summary>
/// Records what the running compute read. It is the <c>read</c> parameter of the compute passed to
/// <see cref="SluiceCache.GetOrSetAsync{TKey, TValue}"/>.
/// </summary>
public sealed class SluiceReads
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _tags = new(StringComparer.Ordinal);
    private readonly IServiceProvider _services;
    private readonly CancellationToken _ct;

    // Made on the first Hold: a hit, and a compute that holds nothing, never needs it.
    private Dictionary<Type, IAsyncDisposable>? _held;
    private bool _open = true;
    private Exception? _violation;

    // The query's name, for messages; the application's services, for what the compute holds; and the compute's
    // token, to hand to fused fetches.
    internal SluiceReads(string query, IServiceProvider services, CancellationToken ct)
    {
        Query = query;
        _services = services;
        _ct = ct;
    }

    internal string Query { get; }

    /// <summary>
    /// Records that the compute read <paramref name="dependency"/>, then runs <paramref name="fetch"/> with the
    /// compute's token and returns its result. If the fetch throws, the dependency stays recorded, so a fallback
    /// the compute returns instead is still evicted by it. Safe to call from concurrent tasks inside one compute.
    /// </summary>
    /// <inheritdoc cref="From{T}(IEnumerable{Dependency}, Func{CancellationToken, Task{T}})" path="/remarks"/>
    /// <exception cref="InvalidOperationException">The compute has already returned; the fetch doesn't run.</exception>
    public Task<T> From<T>(Dependency dependency, Func<CancellationToken, Task<T>> fetch)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return From([dependency], fetch);
    }

    /// <summary>
    /// Records that the compute read <paramref name="dependencies"/>, then runs <paramref name="fetch"/> with the
    /// compute's token and returns its result. If the fetch throws, the dependencies stay recorded.
    /// Safe to call from concurrent tasks inside one compute.
    /// </summary>
    /// <remarks>
    /// The fetch is a declared read: the dependencies cover whatever it reads, so the EF package lets its queries run
    /// on any context, not only <c>read.Db</c>'s, and records nothing more for them. An EF write, a read inside a
    /// transaction, and a call to <see cref="SluiceCache.GetOrSetAsync{TKey, TValue}"/> still throw there.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="dependencies"/> is empty, or holds a null; the fetch doesn't run.
    /// </exception>
    /// <exception cref="InvalidOperationException">The compute has already returned; the fetch doesn't run.</exception>
    public Task<T> From<T>(
        IEnumerable<Dependency> dependencies,
        Func<CancellationToken, Task<T>> fetch
    )
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var tags = Dependency.TagsOf(dependencies, dependency => dependency.ReadTags);
        if (tags.Count == 0)
        {
            throw new ArgumentException(
                "read.From(dependencies, fetch) was given no dependencies. The fetch must declare what it reads: pass "
                    + "the dependency of every source it reads.",
                nameof(dependencies)
            );
        }

        Record(tags);
        return ActiveCompute.Fetch(this, fetch, _ct);
    }

    /// <summary>
    /// Records that the compute read these dependencies, for a value already in hand or a fetch that isn't one call.
    /// Safe to call from concurrent tasks inside one compute.
    /// </summary>
    /// <exception cref="InvalidOperationException">The compute has already returned.</exception>
    public void From(params IEnumerable<Dependency> dependencies) =>
        Record(Dependency.TagsOf(dependencies, dependency => dependency.ReadTags));

    private void Record(List<string> tags)
    {
        lock (_lock)
        {
            if (!_open)
            {
                throw new InvalidOperationException(
                    "read.From was called after the compute returned, so the dependency can't be recorded. "
                        + "Await every read before returning from the compute."
                );
            }

            _tags.UnionWith(tags);
        }
    }

    internal T Hold<T>(Func<IServiceProvider, T> create)
        where T : class, IAsyncDisposable
    {
        lock (_lock)
        {
            if (!_open)
            {
                throw new InvalidOperationException(
                    "The compute has already returned, so it can't open anything more (read.Db included). Await "
                        + "every read before returning from the compute."
                );
            }

            _held ??= [];
            if (_held.TryGetValue(typeof(T), out var held))
            {
                return (T)held;
            }

            var created = create(_services);
            _held.Add(typeof(T), created);
            return created;
        }
    }

    // By reference: a resource that overrides Equals mustn't pass for the compute's own.
    internal bool Holds(IAsyncDisposable resource)
    {
        lock (_lock)
        {
            return _held is not null && _held.Values.Any(held => ReferenceEquals(held, resource));
        }
    }

    // Once the compute has closed, so nothing is added after. Disposes every resource even when one throws, so a
    // pooled context still goes back to its pool, then throws what they threw.
    internal async ValueTask ReleaseAsync()
    {
        if (_held is null)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var held in _held.Values)
        {
            try
            {
                await held.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is [var only])
        {
            ExceptionDispatchInfo.Throw(only);
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Disposing what the compute held threw more than once.",
                failures
            );
        }
    }

    internal bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    internal bool HasViolation
    {
        get
        {
            lock (_lock)
            {
                return _violation is not null;
            }
        }
    }

    internal string[] Close()
    {
        lock (_lock)
        {
            _open = false;
            return [.. _tags];
        }
    }

    internal void Violate(Exception violation)
    {
        lock (_lock)
        {
            _violation ??= violation;
        }
    }

    internal void ThrowIfViolated()
    {
        Exception? violation;
        lock (_lock)
        {
            violation = _violation;
        }

        if (violation is not null)
        {
            ExceptionDispatchInfo.Throw(violation);
        }
    }
}
