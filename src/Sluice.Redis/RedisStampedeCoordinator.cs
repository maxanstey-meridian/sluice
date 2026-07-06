using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisStampedeCoordinator(
    IConnectionMultiplexer redis,
    string keyPrefix = "sluice"
) : IStampedeCoordinator
{
    private readonly IDatabase _db = redis.GetDatabase();

    private const string ReleaseScript = """
        if redis.call("get", KEYS[1]) == ARGV[1] then
            return redis.call("del", KEYS[1])
        else
            return 0
        end
        """;

    public async Task<IRefreshLease?> TryAcquireAsync(
        string entryKey,
        TimeSpan leaseTtl,
        CancellationToken ct
    )
    {
        var token = Guid.NewGuid().ToString("N");
        var lockKey = $"{keyPrefix}:lock:{entryKey}";
        var acquired = await _db.StringSetAsync(lockKey, token, leaseTtl, When.NotExists);
        return acquired ? new Lease(entryKey, lockKey, token, _db) : null;
    }

    public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;

    private sealed class Lease(string entryKey, string lockKey, string token, IDatabase db)
        : IRefreshLease
    {
        private int _disposed;
        private readonly string _token = token;

        public string EntryKey { get; } = entryKey;
        public string Token => _token;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await db.ScriptEvaluateAsync(
                    ReleaseScript,
                    new RedisKey[] { lockKey },
                    new RedisValue[] { _token }
                );
            }
        }
    }
}
