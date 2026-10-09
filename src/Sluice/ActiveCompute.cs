namespace Sluice;

/// <summary>
/// The ambient "a compute is running" flag: the open <see cref="SluiceReads"/> of the current flow, and the scope of
/// the fused fetch it's running in, if any.
/// </summary>
internal static class ActiveCompute
{
    private static readonly AsyncLocal<SluiceReads?> _current = new();

    // The fused fetch running in this flow, if any.
    private static readonly AsyncLocal<FetchScope?> _fetch = new();

    // A closed SluiceReads can linger in a fire-and-forget task's flow; that task is no longer inside a compute.
    internal static SluiceReads? Current
    {
        get => _current.Value is { IsOpen: true } reads ? reads : null;
        set => _current.Value = value;
    }

    /// <summary>
    /// Records a guard violation on the compute running in the current flow, so that call caches nothing and throws
    /// it even if the compute catches it, and returns it for the caller to throw. <paramref name="violation"/> gets
    /// <paramref name="state"/> and whether this flow is inside a fused fetch of that compute, and returns the
    /// violation, or null when there is none. It isn't called, and this returns null, when no compute is running, so
    /// a check on a hot path costs nothing there: pass a static lambda and its input as state, so nothing is
    /// allocated either.
    /// </summary>
    /// <remarks>
    /// A fused fetch (<see cref="SluiceReads.From{T}(IEnumerable{Dependency}, Func{CancellationToken, Task{T}})"/>)
    /// is a declared read: the dependencies recorded with it cover whatever it reads. So a check of where a read
    /// comes from may let it through there; every other check ignores it. The fetch lasts from its call until its task
    /// completes, in its own flow (see <see cref="Fetch{T}"/>): a task it started that runs on after it (unless a fetch
    /// it's nested in is still running), the rest of the compute, and a compute started from the fetch's flow are
    /// outside it. The one internal <c>Sluice.EntityFrameworkCore</c> uses (its strict reads record their violations
    /// here), which is why that package depends on this exact core version.
    /// </remarks>
    internal static Exception? Violate<TState>(
        TState state,
        Func<TState, bool, Exception?> violation
    )
    {
        if (Current is not { } reads)
        {
            return null;
        }

        var exception = violation(state, _fetch.Value?.IsOpenFor(reads) == true);
        if (exception is not null)
        {
            reads.Violate(exception);
        }

        return exception;
    }

    /// <summary>
    /// Runs <paramref name="reads"/>' fused fetch as a declared read, in a scope that its flow and the tasks it starts
    /// inherit, and that ends before the returned task completes. The scope is set inside this async method, so it
    /// doesn't reach the caller, and a scope opened inside it chains to it, so a task an inner fetch leaves running is
    /// still inside the outer fetch while that runs. Not covered: a continuation registered on the fetch's own task
    /// before the fetch returns it can run before the scope ends, and work started under
    /// <see cref="ExecutionContext.SuppressFlow"/> carries no scope (and no compute).
    /// </summary>
    internal static async Task<T> Fetch<T>(
        SluiceReads reads,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct
    )
    {
        var scope = new FetchScope(reads, _fetch.Value);
        _fetch.Value = scope;
        try
        {
            return await fetch(ct).ConfigureAwait(false);
        }
        finally
        {
            scope.End();
        }
    }

    private sealed class FetchScope(SluiceReads reads, FetchScope? outer)
    {
        private volatile bool _ended;

        internal bool IsOpenFor(SluiceReads current) =>
            (!_ended && current == reads) || outer?.IsOpenFor(current) == true;

        internal void End() => _ended = true;
    }
}
