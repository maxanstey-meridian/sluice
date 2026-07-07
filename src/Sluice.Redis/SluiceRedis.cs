using System.Text.Json;
using StackExchange.Redis;

namespace Sluice.Redis;

public static class SluiceRedis
{
    public static SluiceKernel Create(
        IConnectionMultiplexer redis,
        string keyPrefix = "sluice",
        JsonSerializerOptions? serializerOptions = null,
        TimeProvider? clock = null,
        StampedeOptions? stampedeOptions = null,
        RedisCircuitBreakerOptions? circuitBreakerOptions = null
    )
    {
        var breaker = new RedisCircuitBreaker(
            circuitBreakerOptions ?? new RedisCircuitBreakerOptions(),
            clock
        );

        var cache = new ResilientCacheStore(
            new RedisCacheStore(redis, keyPrefix, serializerOptions, clock),
            breaker
        );
        var graph = new ResilientGraphStore(new RedisGraphStore(redis, keyPrefix), breaker);
        var stampede = new ResilientStampedeCoordinator(
            new RedisStampedeCoordinator(redis, keyPrefix),
            breaker
        );

        var epochFence = new RedisEpochFence(redis, keyPrefix);

        return new SluiceKernel(
            cache,
            graph,
            clock,
            stampede,
            stampedeOptions,
            epochFence: epochFence
        );
    }
}
