using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class ResilientCacheStore(ICacheStore inner, RedisCircuitBreaker breaker)
    : ICacheStore
{
    public async Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return null;
        }
        try
        {
            var result = await inner.GetAsync<TValue>(key, ct);
            breaker.RecordSuccess();
            return result;
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
            return null;
        }
    }

    public async Task SetAsync<TValue>(string key, CacheEntry<TValue> entry, CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return;
        }
        try
        {
            await inner.SetAsync(key, entry, ct);
            breaker.RecordSuccess();
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
        }
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return false;
        }
        try
        {
            var result = await inner.RemoveAsync(key, ct);
            breaker.RecordSuccess();
            return result;
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
            return false;
        }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return;
        }
        try
        {
            await inner.ClearAsync(ct);
            breaker.RecordSuccess();
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
        }
    }
}
