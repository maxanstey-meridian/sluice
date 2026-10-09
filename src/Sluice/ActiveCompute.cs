namespace Sluice;

/// <summary>The ambient "a compute is running" flag: the open <see cref="SluiceReads"/> of the current flow.</summary>
internal static class ActiveCompute
{
    private static readonly AsyncLocal<SluiceReads?> _current = new();

    // A closed SluiceReads can linger in a fire-and-forget task's flow; that task is no longer inside a compute.
    internal static SluiceReads? Current
    {
        get => _current.Value is { IsOpen: true } reads ? reads : null;
        set => _current.Value = value;
    }

    /// <summary>
    /// Records a guard violation on the compute running in the current flow, so that call caches nothing and throws
    /// it even if the compute catches it, and returns it for the caller to throw. Returns null, without building the
    /// violation, when no compute is running, so a check on a hot path costs nothing outside computes.
    /// </summary>
    /// <remarks>
    /// The one internal <c>Sluice.EntityFrameworkCore</c> uses (its strict reads record their violations here), which
    /// is why that package depends on this exact core version.
    /// </remarks>
    internal static Exception? Violate(Func<Exception> violation)
    {
        if (Current is not { } reads)
        {
            return null;
        }

        var exception = violation();
        reads.Violate(exception);
        return exception;
    }
}
