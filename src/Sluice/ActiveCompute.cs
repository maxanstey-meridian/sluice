using System.ComponentModel;

namespace Sluice.Infrastructure;

/// <summary>
/// The ambient "a compute is running" flag: the open <see cref="SluiceReads"/> of the current flow, whether the flow
/// is in one of its fused fetches, and what the compute holds until it ends. For Sluice's own packages, which check
/// and record reads against it; it isn't a supported API, and may change in any release.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ActiveCompute
{
    private static readonly AsyncLocal<SluiceReads?> _current = new();

    // The compute whose fused fetch this flow is in, if any.
    private static readonly AsyncLocal<SluiceReads?> _fetch = new();

    // A closed SluiceReads can linger in a fire-and-forget task's flow; that task is no longer inside a compute.
    internal static SluiceReads? Current
    {
        get => _current.Value is { IsOpen: true } reads ? reads : null;
        set => _current.Value = value;
    }

    /// <summary>
    /// The reads of the compute running in the current flow, or null outside one. A check on a hot path reads this
    /// first, so it costs one lookup outside a compute.
    /// </summary>
    public static SluiceReads? Reads => Current;

    /// <summary>
    /// Whether the current flow is in a fused fetch of <paramref name="reads"/>' compute
    /// (<see cref="SluiceReads.From{T}(IEnumerable{Dependency}, Func{CancellationToken, Task{T}})"/>). A fetch is a
    /// declared read: the dependencies recorded with it cover whatever it reads, so a check of where a read comes
    /// from may let it through there; every other check ignores it. The fetch's flow, and the tasks it starts, are
    /// inside it (see <see cref="Fetch{T}"/>); the rest of the compute, and a compute started from the fetch's flow,
    /// are outside it.
    /// </summary>
    /// <param name="reads">The compute's reads.</param>
    public static bool InFetch(SluiceReads reads) => _fetch.Value == reads;

    /// <summary>The name of the query <paramref name="reads"/>' compute is computing, for messages.</summary>
    /// <param name="reads">The compute's reads.</param>
    public static string QueryName(SluiceReads reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        return reads.Query;
    }

    /// <summary>
    /// Records a guard violation on <paramref name="reads"/>' compute, so that call caches nothing and throws it even
    /// if the compute catches it, and returns it for the caller to throw.
    /// </summary>
    /// <param name="reads">The compute's reads.</param>
    /// <param name="violation">The violation.</param>
    /// <returns><paramref name="violation"/>.</returns>
    public static Exception Violate(SluiceReads reads, Exception violation)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(violation);
        reads.Violate(violation);
        return violation;
    }

    /// <summary>
    /// The <typeparamref name="T"/> that <paramref name="reads"/>' compute holds, made by <paramref name="create"/>
    /// from the application's services on first use. The compute disposes it when it ends.
    /// </summary>
    /// <typeparam name="T">The resource; the compute holds one of each type.</typeparam>
    /// <param name="reads">The compute's reads.</param>
    /// <param name="create">Makes the resource from the application's services.</param>
    /// <exception cref="InvalidOperationException">The compute has already returned.</exception>
    public static T Hold<T>(SluiceReads reads, Func<IServiceProvider, T> create)
        where T : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(create);
        return reads.Hold(create);
    }

    /// <summary>Whether <paramref name="reads"/>' compute holds <paramref name="resource"/>.</summary>
    /// <param name="reads">The compute's reads.</param>
    /// <param name="resource">The resource.</param>
    public static bool Holds(SluiceReads reads, object resource)
    {
        ArgumentNullException.ThrowIfNull(reads);
        return reads.Holds(resource);
    }

    /// <summary>
    /// Runs <paramref name="reads"/>' fused fetch as a declared read. The flag is set inside this async method, so
    /// its flow and the tasks it starts inherit it and the caller doesn't: .NET restores the caller's values when the
    /// method returns. Work started under <see cref="ExecutionContext.SuppressFlow"/> carries no flag (and no
    /// compute).
    /// </summary>
    internal static async Task<T> Fetch<T>(
        SluiceReads reads,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct
    )
    {
        _fetch.Value = reads;
        return await fetch(ct).ConfigureAwait(false);
    }
}
