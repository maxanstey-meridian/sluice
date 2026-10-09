using System.Collections.Concurrent;

namespace Playground;

// Playground only: how many times each dashboard was computed, so the page can show which ones a write evicted.
public sealed class ComputeCounts
{
    private readonly ConcurrentDictionary<UserId, int> _counts = new();

    public void Add(UserId userId) => _counts.AddOrUpdate(userId, 1, (_, count) => count + 1);

    public IReadOnlyDictionary<UserId, int> Snapshot() => new Dictionary<UserId, int>(_counts);
}
