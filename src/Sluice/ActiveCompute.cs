namespace Sluice;

/// <summary>The ambient "a compute is running" flag: the open <see cref="SluiceReads"/> of the current flow.</summary>
internal static class ActiveCompute
{
    private static readonly AsyncLocal<SluiceReads?> _current = new();

    // The compute whose fused fetch is running in this flow, if any.
    private static readonly AsyncLocal<SluiceReads?> _fetching = new();

    // A closed SluiceReads can linger in a fire-and-forget task's flow; that task is no longer inside a compute.
    internal static SluiceReads? Current
    {
        get => _current.Value is { IsOpen: true } reads ? reads : null;
        set => _current.Value = value;
    }

    /// <summary>
    /// Records a guard violation on the compute running in the current flow, so that call caches nothing and throws
    /// it even if the compute catches it, and returns it for the caller to throw. <paramref name="violation"/> gets
    /// <paramref name="state"/> and returns the violation, or null when there is none. It isn't called, and this
    /// returns null, when no compute is running or inside one of its fused fetches, so a check on a hot path costs
    /// nothing there: pass a static lambda and its input as state, so nothing is allocated either.
    /// </summary>
    /// <remarks>
    /// A fused fetch (<see cref="SluiceReads.From{T}(IEnumerable{Dependency}, Func{CancellationToken, Task{T}})"/>)
    /// is a declared read: the dependencies recorded with it cover whatever it does, so nothing inside it is a
    /// violation. A compute that starts inside a fetch (a nested <see cref="SluiceCache.GetOrSetAsync"/>) is
    /// checked again. The one internal <c>Sluice.EntityFrameworkCore</c> uses (its strict reads record their
    /// violations here), which is why that package depends on this exact core version.
    /// </remarks>
    internal static Exception? Violate<TState>(TState state, Func<TState, Exception?> violation)
    {
        if (Current is not { } reads || _fetching.Value == reads)
        {
            return null;
        }

        var exception = violation(state);
        if (exception is not null)
        {
            reads.Violate(exception);
        }

        return exception;
    }

    /// <summary>
    /// Runs <paramref name="reads"/>' fused fetch with violations off in its flow, and only there: the flag is set
    /// for the call and restored after it, and an async fetch's continuations keep the value they started with.
    /// </summary>
    internal static Task<T> Fetch<T>(
        SluiceReads reads,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct
    )
    {
        var outer = _fetching.Value;
        _fetching.Value = reads;
        try
        {
            return fetch(ct);
        }
        finally
        {
            _fetching.Value = outer;
        }
    }
}
