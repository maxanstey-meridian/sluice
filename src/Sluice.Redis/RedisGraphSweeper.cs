using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisGraphSweeper(
    IConnectionMultiplexer redis,
    IGraphStore graphStore,
    string keyPrefix = "sluice"
)
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<int> SweepAsync(int batchSize, CancellationToken ct)
    {
        var removed = 0;
        var fwdPrefix = $"{keyPrefix}:fwd:";

        var keys = await redis.ScanKeysAsync($"{fwdPrefix}*", batchSize);

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            var fullKey = (string?)key;
            if (fullKey is null)
            {
                continue;
            }

            var entryKey = fullKey[fwdPrefix.Length..];
            var exists = await _db.KeyExistsAsync($"{keyPrefix}:cache:{entryKey}");

            if (!exists)
            {
                await graphStore.ClearEntryEdges(entryKey, ct);
                removed++;
            }
        }

        return removed;
    }
}
