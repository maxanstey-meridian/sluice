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
    private readonly Dictionary<Type, IAsyncDisposable> _held = [];
    private readonly IServiceProvider _services;
    private readonly CancellationToken _ct;
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
    /// The fetch is a declared read: the dependencies cover whatever it reads, so the EF package lets it run any EF
    /// query, on any context, and records nothing more for it. Calling <see cref="SluiceCache.GetOrSetAsync{TKey, TValue}"/> from it still throws.
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
                    "The compute has already returned, so it can't hold anything more. Await every read before "
                        + "returning from the compute."
                );
            }

            if (_held.TryGetValue(typeof(T), out var held))
            {
                return (T)held;
            }

            var created = create(_services);
            _held.Add(typeof(T), created);
            return created;
        }
    }

    internal bool Holds(object resource)
    {
        lock (_lock)
        {
            return resource is IAsyncDisposable held && _held.ContainsValue(held);
        }
    }

    // Once the compute has closed, so nothing is added after.
    internal async ValueTask ReleaseAsync()
    {
        foreach (var held in _held.Values)
        {
            await held.DisposeAsync().ConfigureAwait(false);
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
