using System.ComponentModel;
using System.Transactions;

namespace Sluice.Infrastructure;

/// <summary>
/// How a package tells <see cref="SluiceCache"/> what the caller's own transactions have written and not yet
/// committed, so a cached read inside them computes fresh, and throws if it depends on those writes (see
/// <see cref="SluiceFlow"/>). For Sluice's own packages; it isn't a supported API, and may change in any release.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class FlowWrites
{
    /// <summary>
    /// Adds a probe to the current flow's <see cref="SluiceFlow"/> box, which every
    /// <see cref="SluiceCache.GetOrSetAsync{TKey, TValue}"/> on <paramref name="cache"/> in the flow calls first, unless
    /// the box already holds one for <paramref name="cache"/> and <paramref name="key"/>: a box keeps one probe per
    /// cache and key. The box keeps it until the flow's outermost <see cref="SluiceFlow.Begin"/> handle is disposed, so
    /// neither the key nor the probe should keep its transaction alive.
    /// </summary>
    /// <param name="cache">The cache whose reads the writes would invalidate.</param>
    /// <param name="key">
    /// What the probe reports on, such as a transaction's id: a value compared with <see cref="object.Equals(object)"/>,
    /// not the transaction itself.
    /// </param>
    /// <param name="pending">
    /// The dependencies the transaction has written and not yet published; empty when there are none (it committed,
    /// rolled back or hasn't written). It may be called from concurrent tasks.
    /// </param>
    /// <returns>Whether the flow has a box; without one, nothing is kept.</returns>
    public static bool AddProbe(
        SluiceCache cache,
        object key,
        Func<IReadOnlyCollection<Dependency>> pending
    )
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(pending);
        return SluiceFlow.AddProbe(cache, key, pending);
    }

    /// <summary>
    /// Registers how to read an ambient transaction's pending writes for <paramref name="cache"/>: every
    /// <see cref="SluiceCache.GetOrSetAsync{TKey, TValue}"/> on it calls <paramref name="pending"/> with the caller's
    /// <see cref="Transaction.Current"/>, read before the compute suppresses it, when there is one. A cache has one;
    /// a later call replaces it.
    /// </summary>
    /// <param name="cache">The cache whose reads the writes would invalidate.</param>
    /// <param name="pending">
    /// The dependencies the transaction has written and not yet published; empty when there are none. It may be
    /// called from concurrent tasks.
    /// </param>
    public static void OnAmbient(
        SluiceCache cache,
        Func<Transaction, IReadOnlyCollection<Dependency>> pending
    )
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(pending);
        cache.OnAmbient(pending);
    }
}
