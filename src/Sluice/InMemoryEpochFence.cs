using System.Collections.Concurrent;

namespace Sluice;

public sealed class InMemoryEpochFence : IEpochFence
{
    private long _writeEpoch;
    private readonly ConcurrentQueue<InvalidationRecord> _recentInvalidations = new();
    private const int MaxRecentInvalidations = 256;

    public Task<long> ReadEpochAsync(CancellationToken ct) =>
        Task.FromResult(Interlocked.Read(ref _writeEpoch));

    public Task<long> IncrementEpochAsync(
        IReadOnlyList<ResourceAddress> addresses,
        CancellationToken ct
    )
    {
        var epoch = Interlocked.Increment(ref _writeEpoch);
        _recentInvalidations.Enqueue(new InvalidationRecord(epoch, addresses));
        while (_recentInvalidations.Count > MaxRecentInvalidations)
        {
            _recentInvalidations.TryDequeue(out _);
        }
        return Task.FromResult(epoch);
    }

    public Task<bool> HasOverlappingInvalidationAsync(
        long afterEpoch,
        long throughEpoch,
        IReadOnlyList<ResourceAddress> observedReads,
        CancellationToken ct
    )
    {
        if (throughEpoch - afterEpoch >= MaxRecentInvalidations)
        {
            return Task.FromResult(true);
        }

        var snapshot = _recentInvalidations.ToArray();
        foreach (var record in snapshot)
        {
            if (record.Epoch <= afterEpoch)
            {
                continue;
            }

            if (!record.Addresses.Any(changed => EpochFenceHelper.Overlaps(changed, observedReads)))
            {
                continue;
            }

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}
