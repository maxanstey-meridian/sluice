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

    // What runs once the compute returns, before its value is stored: made on the first step, kept in order.
    private List<Func<CancellationToken, Task>>? _beforeStore;

    // Set once the steps have run: a step added after that, before the compute closes (by a task it didn't await),
    // would never run, so it throws rather than leave out what it would record.
    private bool _stepsRun;
    private bool _open = true;
    private bool _uncached;
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

    /// <summary>
    /// Leaves the compute's value uncached: the caller gets it, but it isn't stored, so the next call for the key
    /// computes again. For a value not worth keeping, such as a miss on a key that comes from public input, which
    /// would otherwise let any caller fill memory: <c>if (form is null) { read.DoNotCache(); return null; }</c>.
    /// The compute then needn't record a dependency. A caller waiting on the same key computes its own value once the
    /// compute ends, since nothing is stored for it to read. Safe to call from concurrent tasks inside one compute,
    /// and from a fused fetch.
    /// </summary>
    /// <remarks>
    /// A violation still wins: a compute that read what Sluice can't track throws, whether or not it called this.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The compute has already returned, or this is another compute's read, used inside a running compute.
    /// </exception>
    public void DoNotCache()
    {
        ThrowIfAnotherCompute();
        lock (_lock)
        {
            if (!_open)
            {
                throw new InvalidOperationException(
                    "read.DoNotCache was called after the compute returned, so its value may already be cached. "
                        + "Call it before returning from the compute."
                );
            }

            _uncached = true;
        }
    }

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

            if (Find<T>() is { } held)
            {
                return held;
            }

            var created = create(_services);
            (_held ??= []).Add((typeof(T), created));
            return created;
        }
    }

    internal void BeforeStore(Func<CancellationToken, Task> step)
    {
        ThrowIfAnotherCompute();
        lock (_lock)
        {
            if (!_open || _stepsRun)
            {
                throw new InvalidOperationException(
                    "The compute has already returned, so nothing more can run before its value is stored "
                        + "(read.Entity included). Await every read before returning from the compute."
                );
            }

            (_beforeStore ??= []).Add(step);
        }
    }

    // Once the compute has returned, while it's still open, so a step can record, hold and add steps. In order, with
    // the compute's token; a step added while they run runs too. Stops once the compute has a violation, or asked not
    // to be cached unless checked (the flow has uncommitted writes, and what the steps record is checked against
    // them), since nothing would use what they record. A step that throws stops the rest, and fails the compute.
    internal async Task RunBeforeStoreAsync(bool checkedAgainstWrites)
    {
        for (var i = 0; ; i++)
        {
            Func<CancellationToken, Task> step;
            lock (_lock)
            {
                if (
                    _beforeStore is null
                    || i >= _beforeStore.Count
                    || _violation is not null
                    || (_uncached && !checkedAgainstWrites)
                )
                {
                    _stepsRun = true;
                    return;
                }

                step = _beforeStore[i];
            }

            await step(_ct).ConfigureAwait(false);
        }
    }

    // The T the compute holds, if it has made one. Makes nothing.
    internal T? Held<T>()
        where T : class, IAsyncDisposable
    {
        lock (_lock)
        {
            return Find<T>();
        }
    }

    private T? Find<T>()
        where T : class, IAsyncDisposable
    {
        if (_held is null)
        {
            return null;
        }

        foreach (var (type, resource) in _held)
        {
            if (type == typeof(T))
            {
                return (T)resource;
            }
        }

        return null;
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
    // one's value is built from, so this one would be cached without it; its DoNotCache would leave that compute
    // uncached instead of this one. The running compute gets the violation, so it caches nothing even if it catches
    // the exception.
    private void ThrowIfAnotherCompute()
    {
        if (ActiveCompute.Reads is { } current && current != this)
        {
            throw ActiveCompute.Violate(
                current,
                new InvalidOperationException(
                    $"The compute of query {current.Query} used the read of another compute (query {Query}"
                        + (IsOpen ? "" : ", which has already returned")
                        + "), so it would act on that compute instead of this one. Use the read the compute was given."
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

    // The recorded tags, and whether the compute asked not to be cached. Nothing changes either after this.
    internal (string[] Tags, bool Uncached) Close()
    {
        lock (_lock)
        {
            _open = false;
            return ([.. _tags], _uncached);
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
