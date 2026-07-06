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
        var cursor = 0L;
        var fwdPrefix = $"{keyPrefix}:fwd:";

        do
        {
            ct.ThrowIfCancellationRequested();

            var result = await _db.ExecuteAsync(
                "SCAN",
                cursor.ToString(),
                "MATCH",
                $"{fwdPrefix}*",
                "COUNT",
                batchSize.ToString()
            );

            var inner = (RedisResult[])result!;
            cursor = long.Parse((string?)inner[0] ?? "0");
            var keys = (RedisResult[])inner[1]!;

            foreach (var key in keys)
            {
                var fullKey = (string?)key;
                if (fullKey is null)
                {
                    continue;
                }

                var entryKey = fullKey.Substring(fwdPrefix.Length);
                var exists = await _db.KeyExistsAsync($"{keyPrefix}:cache:{entryKey}");

                if (!exists)
                {
                    await graphStore.ClearEntryEdges(entryKey, ct);
                    removed++;
                }
            }
        } while (cursor != 0);

        return removed;
    }
}
