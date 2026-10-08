namespace Sluice;

/// <summary>
/// The ambient "a compute is running" flag. <see cref="IsActive"/> is the one internal that
/// Sluice.EntityFrameworkCore may use (DESIGN §5.3); that package must pin the exact core version.
/// </summary>
internal static class ActiveCompute
{
    private static readonly AsyncLocal<SluiceReads?> _current = new();

    internal static bool IsActive => Current is not null;

    // A closed SluiceReads can linger in a fire-and-forget task's flow; that task is no longer inside a compute.
    internal static SluiceReads? Current
    {
        get => _current.Value is { IsOpen: true } reads ? reads : null;
        set => _current.Value = value;
    }
}
