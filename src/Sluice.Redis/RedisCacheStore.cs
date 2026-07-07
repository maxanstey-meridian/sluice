using System.Text.Json;
using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisCacheStore(
    IConnectionMultiplexer redis,
    string keyPrefix = "sluice",
    JsonSerializerOptions? serializerOptions = null,
    TimeProvider? clock = null
) : ICacheStore
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct)
    {
        var value = await _db.StringGetAsync($"{keyPrefix}:cache:{key}");
        if (!value.HasValue)
        {
            return null;
        }
        var json = (string?)value!;
        return JsonSerializer.Deserialize<CacheEntry<TValue>>(json ?? "", serializerOptions);
    }

    public async Task SetAsync<TValue>(string key, CacheEntry<TValue> entry, CancellationToken ct)
    {
        var serialized = JsonSerializer.Serialize(entry, serializerOptions);
        if (entry.ExpiresAt is { } expiresAt)
        {
            var diff = expiresAt - _clock.GetUtcNow();
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
        var keys = await redis.ScanKeysAsync($"{keyPrefix}:cache:*");
        if (keys.Count > 0)
        {
            await _db.KeyDeleteAsync([.. keys]);
        }
    }
}
