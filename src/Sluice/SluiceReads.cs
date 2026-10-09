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

    // Made on the first Hold: a hit, and a compute that holds nothing, never needs it. In creation order, so they're
    // disposed in reverse: one made later may use one made earlier.
    private List<(Type Type, IAsyncDisposable Resource)>? _held;
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
    /// <exception cref="InvalidOperationException">
    /// The compute has already returned, or this is another compute's read, used inside a running compute; the fetch
    /// doesn't run.
    /// </exception>
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
    /// <exception cref="InvalidOperationException">
    /// The compute has already returned, or this is another compute's read, used inside a running compute; the fetch
    /// doesn't run.
    /// </exception>
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
    /// <exception cref="InvalidOperationException">
    /// The compute has already returned, or this is another compute's read, used inside a running compute.
    /// </exception>
    public void From(params IEnumerable<Dependency> dependencies) =>
        Record(Dependency.TagsOf(dependencies, dependency => dependency.ReadTags));

    private void Record(List<string> tags)
    {
        ThrowIfAnotherCompute();
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
        ThrowIfAnotherCompute();
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
            foreach (var (type, resource) in _held)
            {
                if (type == typeof(T))
                {
                    return (T)resource;
                }
            }

            var created = create(_services);
            _held.Add((typeof(T), created));
            return created;
        }
    }

    // By reference: a resource that overrides Equals mustn't pass for the compute's own.
    internal bool Holds(IAsyncDisposable resource)
    {
        lock (_lock)
        {
            if (_held is null)
            {
                return false;
            }

            foreach (var (_, held) in _held)
            {
                if (ReferenceEquals(held, resource))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Once the compute has closed, so nothing is added after. Disposes every resource, newest first, even when one
    // throws, so a pooled context still goes back to its pool, then returns what they threw, or null.
    internal async ValueTask<List<Exception>?> ReleaseAsync()
    {
        if (_held is null)
        {
            return null;
        }

        List<Exception>? failures = null;
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            try
            {
                await _held[i].Resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        return failures;
    }

    // A read captured from another compute would record on that compute (or nowhere, once it has returned) what this
    // one's value is built from, so this one would be cached without it. The running compute gets the violation, so it
    // caches nothing even if it catches the exception.
    private void ThrowIfAnotherCompute()
    {
        if (ActiveCompute.Reads is { } current && current != this)
        {
            throw ActiveCompute.Violate(
                current,
                new InvalidOperationException(
                    $"The compute of query {current.Query} used the read of another compute (query {Query}"
                        + (IsOpen ? "" : ", which has already returned")
                        + "), so what it reads wouldn't be recorded on this one. Use the read the compute was given."
                )
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
