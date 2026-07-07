using StackExchange.Redis;

namespace Sluice.Redis;

internal static class RedisScan
{
    public static async Task<List<RedisKey>> ScanKeysAsync(
        this IConnectionMultiplexer redis,
        string pattern,
        int pageSize = 500
    )
    {
        var endpoint = redis.GetEndPoints()[0];
        var server = redis.GetServer(endpoint);
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: pageSize))
        {
            keys.Add(key);
        }
        return keys;
    }
}
