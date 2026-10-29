namespace Sluice;

/// <summary>
/// Marks an entry point, such as a request or a message handler, so Sluice can see the transactions it opens. Inside
/// it, a cached read that depends on writes the flow's own transaction has saved but not committed throws, rather than
/// return data without them; any other cached read while such writes are pending computes fresh and stores nothing.
/// <see cref="InvalidateNow"/> marks a stretch of the flow whose writes must vanish at once, even from queries with
/// <c>MaxStaleness</c>.
/// </summary>
/// <remarks>
/// Sluice sees a transaction's writes through the package that made them, such as the EF Core package. An ambient
/// <c>TransactionScope</c> is seen without this. Another transaction is checked from the flow's first write in it
/// after <see cref="Begin"/>, even one begun before it; one the flow wrote in only before <see cref="Begin"/>, or only
/// on a flow without it, isn't checked.
/// </remarks>
public static class SluiceFlow
{
    private static readonly AsyncLocal<Box?> _box = new();
    private static readonly AsyncLocal<bool> _now = new();

    /// <summary>
    /// Starts tracking the current flow's transactions, until the returned handle is disposed: <c>using var _ =
    /// SluiceFlow.Begin();</c> at the top of the entry point. Tasks the flow starts share it. Inside a flow that has
    /// already begun, this returns a handle that does nothing, so the outer one still tracks.
    /// </summary>
    /// <returns>The handle; dispose it on the flow that began it.</returns>
    public static IDisposable Begin()
    {
        if (_box.Value is not null)
        {
            return new Handle(null);
        }

        var box = new Box();
        _box.Value = box;
        return new Handle(box);
    }

    /// <summary>
    /// Makes everything the current flow invalidates publish hard, as
    /// <see cref="SluiceCache.InvalidateNowAsync(IEnumerable{Dependency})"/> does, until the returned scope is disposed:
    /// <c>using (SluiceFlow.InvalidateNow()) { ... }</c> around a write that must vanish at once, such as an erasure.
    /// That's the writes a package such as the EF Core package captures here, and the app's own
    /// <see cref="SluiceCache.InvalidateAsync(IEnumerable{Dependency})"/> and
    /// <see cref="SluiceCache.Invalidate(IEnumerable{Dependency})"/> calls made here. Tasks the flow starts inside it
    /// keep it. It doesn't need <see cref="Begin"/>.
    /// </summary>
    /// <remarks>
    /// A package's write publishes hard when it started inside the scope, wherever and whenever it publishes: a save
    /// inside it whose transaction commits after the scope is disposed publishes hard, and one made before it whose
    /// transaction commits inside it doesn't. Open it in the method that writes, or one that calls it: an
    /// <c>AsyncLocal</c> set inside an <c>async</c> helper is undone when the helper returns.
    /// </remarks>
    /// <returns>The scope; disposing it puts back what the flow had, so a nested scope leaves the outer one on.</returns>
    public static IDisposable InvalidateNow()
    {
        var previous = _now.Value;
        _now.Value = true;
        return new NowScope(previous);
    }

    // Whether the current flow is inside InvalidateNow: read once, on the writing flow, when a write starts.
    internal static bool Now => _now.Value;

    // Keeps a probe in the current flow's box, unless it holds one for this cache and key; false when the flow has no
    // box.
    internal static bool AddProbe(
        SluiceCache cache,
        object key,
        Func<IReadOnlyCollection<Dependency>> pending
    )
    {
        if (_box.Value is not { } box)
        {
            return false;
        }

        box.Add(cache, key, pending);
        return true;
    }

    // What the current flow's probes for this cache have pending, or null when nothing is. One lookup without a box.
    internal static List<Dependency>? Pending(SluiceCache cache) => _box.Value?.Pending(cache);

    // A flow may fan out, so probes can be added from concurrent tasks. One probe per cache and key: a package adds one
    // each time it sees the flow write in a transaction, and a second for the same transaction would only report the
    // same writes again.
    private sealed class Box
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<
            (SluiceCache Cache, object Key),
            Func<IReadOnlyCollection<Dependency>>
        > _probes = [];

        public void Add(
            SluiceCache cache,
            object key,
            Func<IReadOnlyCollection<Dependency>> pending
        )
        {
            lock (_lock)
            {
                _probes.TryAdd((cache, key), pending);
            }
        }

        // The probes run outside the lock: they're the package's code, and may take its own locks.
        public List<Dependency>? Pending(SluiceCache cache)
        {
            Func<IReadOnlyCollection<Dependency>>[] probes;
            lock (_lock)
            {
                if (_probes.Count == 0)
                {
                    return null;
                }

                probes =
                [
                    .. _probes
                        .Where(probe => probe.Key.Cache == cache)
                        .Select(probe => probe.Value),
                ];
            }

            List<Dependency>? pending = null;
            foreach (var probe in probes)
            {
                if (probe() is { Count: > 0 } dependencies)
                {
                    (pending ??= []).AddRange(dependencies);
                }
            }

            return pending;
        }
    }

    // KeyPin's save-and-restore scope: a sync call sets the flow's own value, which would otherwise outlive it.
    private sealed class NowScope(bool previous) : IDisposable
    {
        public void Dispose() => _now.Value = previous;
    }

    // The outermost handle removes its box; a nested one, holding none, does nothing.
    private sealed class Handle(Box? box) : IDisposable
    {
        public void Dispose()
        {
            if (box is not null && _box.Value == box)
            {
                _box.Value = null;
            }
        }
    }
}
