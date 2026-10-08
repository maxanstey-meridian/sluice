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
    /// Whether the current flow is inside a compute. The one internal <c>Sluice.EntityFrameworkCore</c> uses,
    /// which is why that package depends on this exact core version.
    /// </summary>
    internal static bool IsActive => Current is not null;
}
