namespace Sluice;

/// <summary>The ambient "a compute is running" flag: the open <see cref="SluiceReads"/> of the current flow.</summary>
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
    /// completes, in its own flow: a task it started that runs on after it, the rest of the compute, and a compute
    /// started from the fetch's flow are outside it. The one internal <c>Sluice.EntityFrameworkCore</c> uses (its
    /// strict reads record their violations here), which is why that package depends on this exact core version.
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
    /// Runs <paramref name="reads"/>' fused fetch as a declared read in its own flow: the scope is set for the call and
    /// restored after it, so an async fetch's continuations keep it, and it ends when the fetch's task completes (or
    /// the fetch throws), so nothing the fetch left running is inside it after that.
    /// </summary>
    internal static Task<T> Fetch<T>(
        SluiceReads reads,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct
    )
    {
        var outer = _fetch.Value;
        var scope = new FetchScope(reads);
        _fetch.Value = scope;
        Task<T>? task = null;
        try
        {
            task = fetch(ct);
            return task;
        }
        finally
        {
            _fetch.Value = outer;
            scope.EndWith(task);
        }
    }

    private sealed class FetchScope(SluiceReads reads)
    {
        private volatile bool _ended;

        internal bool IsOpenFor(SluiceReads current) => !_ended && current == reads;

        // Ends now if the fetch threw or has completed, else as its task completes. The continuation is registered
        // before the fetch's caller can await the task, and runs synchronously, so it runs before an await resumes.
        internal void EndWith(Task? task)
        {
            if (task is null || task.IsCompleted)
            {
                _ended = true;
                return;
            }

            _ = task.ContinueWith(
                static (_, scope) => ((FetchScope)scope!)._ended = true,
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }
}
