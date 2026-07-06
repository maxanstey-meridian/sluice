using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class ResilientStampedeCoordinator(
    IStampedeCoordinator inner,
    RedisCircuitBreaker breaker
) : IStampedeCoordinator
{
    public async Task<IRefreshLease?> TryAcquireAsync(
        string entryKey,
        TimeSpan leaseTtl,
        CancellationToken ct
    )
    {
        if (!breaker.TryAllowCall())
        {
            return null;
        }
        try
        {
            var result = await inner.TryAcquireAsync(entryKey, leaseTtl, ct);
            breaker.RecordSuccess();
            return result;
        }
        catch (RedisException)
        {
            breaker.RecordFailure();
            return null;
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
