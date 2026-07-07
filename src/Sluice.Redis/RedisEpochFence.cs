using System.Text.Json;
using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisEpochFence(IConnectionMultiplexer redis, string keyPrefix = "sluice")
    : IEpochFence
{
    private readonly IDatabase _db = redis.GetDatabase();
    private const int MaxRecentInvalidations = 256;

    public async Task<long> ReadEpochAsync(CancellationToken ct)
    {
        var value = await _db.StringGetAsync($"{keyPrefix}:epoch");
        if (!value.HasValue)
        {
            return 0;
        }
        return (long)value;
    }

    public async Task<long> IncrementEpochAsync(
        IReadOnlyList<ResourceAddress> addresses,
        CancellationToken ct
    )
    {
        var epoch = await _db.StringIncrementAsync($"{keyPrefix}:epoch");

        var record = new InvalidationRecord(epoch, addresses);
        var json = JsonSerializer.Serialize(record);
        await _db.SortedSetAddAsync($"{keyPrefix}:inval", json, epoch);

        await _db.SortedSetRemoveRangeByScoreAsync(
            $"{keyPrefix}:inval",
            double.NegativeInfinity,
            epoch - MaxRecentInvalidations
        );

        return epoch;
    }

    public async Task<bool> HasOverlappingInvalidationAsync(
        long afterEpoch,
        long throughEpoch,
        IReadOnlyList<ResourceAddress> observedReads,
        CancellationToken ct
    )
    {
        if (throughEpoch - afterEpoch >= MaxRecentInvalidations)
        {
            return true;
        }

        var entries = await _db.SortedSetRangeByScoreAsync(
            $"{keyPrefix}:inval",
            afterEpoch + 1,
            throughEpoch
        );

        foreach (var entry in entries)
        {
            var json = (string?)entry;
            if (json is null)
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<InvalidationRecord>(json);
            if (record is null)
            {
                continue;
            }

            if (record.Addresses.Any(changed => EpochFenceHelper.Overlaps(changed, observedReads)))
            {
                return true;
            }
        }

        return false;
    }
}
