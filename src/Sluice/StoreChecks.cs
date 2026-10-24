using System.ComponentModel;

namespace Sluice.Infrastructure;

/// <summary>
/// How a package refuses a computed value before <see cref="SluiceCache"/> stores it, such as the EF Core package's
/// check for an entity that would load related rows after the compute. For Sluice's own packages; it isn't a
/// supported API, and may change in any release.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class StoreChecks
{
    /// <summary>
    /// Adds a check that every value <paramref name="cache"/> is about to store goes through: once the compute and
    /// its before-store steps have returned, on the compute's flow, and only when the value will be stored (not when
    /// the compute threw, recorded a violation or no dependencies, called <see cref="SluiceReads.DoNotCache"/>, or ran
    /// while the flow had uncommitted writes). A null value isn't checked. The first check that returns an exception
    /// makes it the call's violation: nothing is stored and the caller gets it. A check already added isn't added
    /// again.
    /// </summary>
    /// <param name="cache">The cache whose values to check.</param>
    /// <param name="check">
    /// Returns why the value mustn't be stored, or null to let it through. It may be called from concurrent computes.
    /// </param>
    public static void Add(SluiceCache cache, Func<object, Exception?> check)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(check);
        cache.AddStoreCheck(check);
    }
}
