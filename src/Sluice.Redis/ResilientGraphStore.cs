using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class ResilientGraphStore(IGraphStore inner, RedisCircuitBreaker breaker)
    : IGraphStore
{
    public async Task<IReadOnlyList<string>> FindAffectedEntries(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    )
    {
        if (!breaker.TryAllowCall())
        {
            return [];
        }
        try
        {
            var result = await inner.FindAffectedEntries(changedAddresses, ct);
            breaker.RecordSuccess();
            return result;
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
            return [];
        }
    }

    public async Task RecordEntry(
        string entryKey,
        IReadOnlyList<ResourceAddress> addresses,
        DateTimeOffset cachedAt,
        CancellationToken ct
    )
    {
        if (!breaker.TryAllowCall())
        {
            return;
        }
        try
        {
            await inner.RecordEntry(entryKey, addresses, cachedAt, ct);
            breaker.RecordSuccess();
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
        }
    }

    public async Task ClearEntryEdges(string entryKey, CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return;
        }
        try
        {
            await inner.ClearEntryEdges(entryKey, ct);
            breaker.RecordSuccess();
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return;
        }
        try
        {
            await inner.FlushAsync(ct);
            breaker.RecordSuccess();
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
        }
    }

    public async Task<string> DumpGraphAsync(CancellationToken ct)
    {
        if (!breaker.TryAllowCall())
        {
            return "";
        }
        try
        {
            var result = await inner.DumpGraphAsync(ct);
            breaker.RecordSuccess();
            return result;
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
            return "";
        }
    }
}
