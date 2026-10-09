using System.ComponentModel;

namespace Sluice.Infrastructure;

/// <summary>
/// The ambient "a compute is running" flag: the open <see cref="SluiceReads"/> of the current flow, and whether the
/// flow is in one of its fused fetches. For Sluice's own packages, which check reads against it; it isn't a supported
/// API, and may change in any release.
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
    /// comes from may let it through there; every other check ignores it. The fetch's flow, and the tasks it starts,
    /// are inside it (see <see cref="Fetch{T}"/>); the rest of the compute, and a compute started from the fetch's
    /// flow, are outside it.
    /// </remarks>
    /// <typeparam name="TState">The check's input.</typeparam>
    /// <param name="state">The check's input, passed to <paramref name="violation"/>.</param>
    /// <param name="violation">The check: given the state and whether the flow is in a fused fetch.</param>
    /// <returns>The violation recorded, or null.</returns>
    public static Exception? Violate<TState>(TState state, Func<TState, bool, Exception?> violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        if (Current is not { } reads)
        {
            return null;
        }

        var exception = violation(state, _fetch.Value == reads);
        if (exception is not null)
        {
            reads.Violate(exception);
        }

        return exception;
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
