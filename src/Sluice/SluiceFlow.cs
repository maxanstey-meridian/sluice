namespace Sluice;

/// <summary>
/// Marks an entry point, such as a request or a message handler, so Sluice can see the transactions it opens. Inside
/// it, a cached read that depends on writes the flow's own transaction has saved but not committed throws, rather than
/// return data without them; any other cached read while such writes are pending computes fresh and stores nothing.
/// </summary>
/// <remarks>
/// Sluice sees a transaction's writes through the package that made them, such as the EF Core package. An ambient
/// <c>TransactionScope</c> is seen without this; an EF transaction begun before <see cref="Begin"/>, or on a flow
/// without it, isn't checked.
/// </remarks>
public static class SluiceFlow
{
    private static readonly AsyncLocal<Box?> _box = new();

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

    // Keeps a probe in the current flow's box; false when the flow has none.
    internal static bool AddProbe(SluiceCache cache, Func<IReadOnlyCollection<Dependency>> pending)
    {
        if (_box.Value is not { } box)
        {
            return false;
        }

        box.Add(cache, pending);
        return true;
    }

    // What the current flow's probes for this cache have pending, or null when nothing is. One lookup without a box.
    internal static List<Dependency>? Pending(SluiceCache cache) => _box.Value?.Pending(cache);

    // A flow may fan out, so probes can be added from concurrent tasks.
    private sealed class Box
    {
        private readonly Lock _lock = new();
        private readonly List<(
            SluiceCache Cache,
            Func<IReadOnlyCollection<Dependency>> Pending
        )> _probes = [];

        public void Add(SluiceCache cache, Func<IReadOnlyCollection<Dependency>> pending)
        {
            lock (_lock)
            {
                _probes.Add((cache, pending));
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
                    .. _probes.Where(probe => probe.Cache == cache).Select(probe => probe.Pending),
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
