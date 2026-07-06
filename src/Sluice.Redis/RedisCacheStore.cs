using System.Text.Json;
using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisCacheStore(IConnectionMultiplexer redis, string keyPrefix = "sluice")
    : ICacheStore
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct)
    {
        var value = await _db.StringGetAsync($"{keyPrefix}:cache:{key}");
        if (!value.HasValue)
        {
            return null;
        }
        var json = (string?)value!;
        return JsonSerializer.Deserialize<CacheEntry<TValue>>(json ?? "");
    }

    public async Task SetAsync<TValue>(string key, CacheEntry<TValue> entry, CancellationToken ct)
    {
        var serialized = JsonSerializer.Serialize(entry);
        if (entry.ExpiresAt is { } expiresAt)
        {
            var diff = expiresAt - DateTimeOffset.UtcNow;
            if (diff <= TimeSpan.Zero)
            {
                await _db.KeyDeleteAsync($"{keyPrefix}:cache:{key}");
                return;
            }
            await _db.StringSetAsync(
                $"{keyPrefix}:cache:{key}",
                serialized,
                diff,
                false,
                When.Always,
                CommandFlags.None
            );
        }
        else
        {
            await _db.StringSetAsync(
                $"{keyPrefix}:cache:{key}",
                serialized,
                null,
                false,
                When.Always,
                CommandFlags.None
            );
        }
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct) =>
        await _db.KeyDeleteAsync($"{keyPrefix}:cache:{key}");

    public async Task ClearAsync(CancellationToken ct)
    {
        var keys = new List<RedisKey>();
        var cursor = 0L;
        do
        {
            var result = await _db.ExecuteAsync(
                "SCAN",
                cursor.ToString(),
                "MATCH",
                $"{keyPrefix}:cache:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            cursor = long.Parse((string?)inner[0] ?? "0");
            var items = (RedisResult[])inner[1]!;
            foreach (var item in items)
            {
                var bytes = (byte[]?)(RedisResult?)item;
                keys.Add(bytes is not null ? (RedisKey)bytes : new RedisKey((string?)item!));
            }
        } while (cursor != 0);

        if (keys.Count > 0)
        {
            await _db.KeyDeleteAsync([.. keys]);
        }
    }
}
