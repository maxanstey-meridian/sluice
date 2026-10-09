using System.Runtime.ExceptionServices;

namespace Sluice;

/// <summary>
/// Records what the running compute read. It is the <c>read</c> parameter of the compute passed to
/// <see cref="SluiceCache.GetOrSetAsync"/>.
/// </summary>
public sealed class SluiceReads
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _tags = new(StringComparer.Ordinal);
    private readonly CancellationToken _ct;
    private bool _open = true;
    private Exception? _violation;

    // The compute's token, held only to hand to fused fetches.
    internal SluiceReads(CancellationToken ct) => _ct = ct;

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
    /// The fetch is a declared read: the dependencies cover whatever it reads, so the EF package's strict reads let it
    /// run plain EF queries until its task completes; a task it leaves running is outside it after that. Sluice's
    /// other guards still apply inside it: calling <see cref="SluiceCache.GetOrSetAsync"/> from it throws, and the EF
    /// package still checks its read helpers' queries.
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
