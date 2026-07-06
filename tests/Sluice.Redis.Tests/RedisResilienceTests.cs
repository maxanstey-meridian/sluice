using FluentAssertions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Sluice.Redis.Tests;

public sealed class RedisResilienceTests
{
    [RequireDockerFact]
    public async Task FailOpen_ReturnsSafeDefaults_WhenRedisUnavailable()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var options = new RedisCircuitBreakerOptions
        {
            FailureThreshold = 3,
            CooldownDuration = TimeSpan.FromMilliseconds(200),
        };
        var breaker = new RedisCircuitBreaker(options);
        var innerCache = new RedisCacheStore(redis, "sluice-failopen");
        var cache = new ResilientCacheStore(innerCache, breaker);
        var innerGraph = new RedisGraphStore(redis, "sluice-failopen");
        var graph = new ResilientGraphStore(innerGraph, breaker);
        var innerStampede = new RedisStampedeCoordinator(redis, "sluice-failopen");
        var stampede = new ResilientStampedeCoordinator(innerStampede, breaker);

        await container.StopAsync();
        await Task.Delay(500);

        for (int i = 0; i < 3; i++)
        {
            await cache.GetAsync<string>($"key-{i}", CancellationToken.None);
        }

        breaker.State.Should().Be(BreakerState.Open);

        var get = await cache.GetAsync<string>("any-key", CancellationToken.None);
        get.Should().BeNull();

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(5);
        await cache.SetAsync(
            "set-key",
            new CacheEntry<string>("value", [], now, expiresAt),
            CancellationToken.None
        );

        var remove = await cache.RemoveAsync("remove-key", CancellationToken.None);
        remove.Should().BeFalse();

        await graph.FindAffectedEntries(
            [new ResourceAddress(ResourceKind.Entity, "user", "alice")],
            CancellationToken.None
        );
        await graph.RecordEntry("entry", [], DateTimeOffset.UtcNow, CancellationToken.None);
        await graph.ClearEntryEdges("entry", CancellationToken.None);
        await graph.FlushAsync(CancellationToken.None);
        var dump = await graph.DumpGraphAsync(CancellationToken.None);
        dump.Should().BeEmpty();

        var stampedeResult = await stampede.TryAcquireAsync(
            "lease",
            TimeSpan.FromSeconds(30),
            CancellationToken.None
        );
        stampedeResult.Should().BeNull();
        await stampede.ClearAsync(CancellationToken.None);
    }

    [RequireDockerFact]
    public async Task Circuit_Recovery_ResumesCaching_AfterRestart()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var options = new RedisCircuitBreakerOptions
        {
            FailureThreshold = 3,
            CooldownDuration = TimeSpan.FromMilliseconds(500),
        };
        var breaker = new RedisCircuitBreaker(options);
        var cache = new ResilientCacheStore(new RedisCacheStore(redis, "sluice-recovery"), breaker);

        await container.StopAsync();
        await Task.Delay(1000);

        for (int i = 0; i < 3; i++)
        {
            await cache.GetAsync<string>($"fail-key-{i}", CancellationToken.None);
        }

        breaker.State.Should().Be(BreakerState.Open);

        await Task.Delay(options.CooldownDuration + TimeSpan.FromMilliseconds(500));

        await container.StartAsync();

        var freshRedis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var db = freshRedis.GetDatabase();
        var pingTimeout = TimeSpan.FromSeconds(10);
        var pingStart = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - pingStart < pingTimeout)
        {
            try
            {
                await db.PingAsync();
                break;
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        var recoveryCache = new ResilientCacheStore(
            new RedisCacheStore(freshRedis, "sluice-recovery"),
            breaker
        );

        var probeResult = await recoveryCache.GetAsync<string>("probe-key", CancellationToken.None);
        probeResult.Should().BeNull();
        breaker.State.Should().Be(BreakerState.Closed);

        await recoveryCache.SetAsync(
            "recovery-key",
            new CacheEntry<string>(
                "restored",
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)
            ),
            CancellationToken.None
        );
        var readBack = await recoveryCache.GetAsync<string>("recovery-key", CancellationToken.None);
        readBack.Should().NotBeNull();
        readBack!.Value.Should().Be("restored");

        await redis.DisposeAsync();
        await freshRedis.DisposeAsync();
    }

    [RequireDockerFact]
    public async Task SharedBreaker_FailsAllStores_WhenRedisDown()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var options = new RedisCircuitBreakerOptions
        {
            FailureThreshold = 2,
            CooldownDuration = TimeSpan.FromMilliseconds(200),
        };
        var breaker = new RedisCircuitBreaker(options);

        var cache = new ResilientCacheStore(new RedisCacheStore(redis, "sluice-shared"), breaker);
        var graph = new ResilientGraphStore(new RedisGraphStore(redis, "sluice-shared"), breaker);

        await container.StopAsync();
        await Task.Delay(500);

        await cache.GetAsync<string>("k1", CancellationToken.None);
        await graph.FindAffectedEntries(
            [new ResourceAddress(ResourceKind.Entity, "user", "bob")],
            CancellationToken.None
        );

        breaker.State.Should().Be(BreakerState.Open);

        var cacheGet = await cache.GetAsync<string>("should-shortcircuit", CancellationToken.None);
        cacheGet.Should().BeNull();

        var graphResult = await graph.FindAffectedEntries(
            [new ResourceAddress(ResourceKind.Entity, "user", "carol")],
            CancellationToken.None
        );
        graphResult.Should().BeEmpty();
    }

    [RequireDockerFact]
    public async Task GraphDump_FailsOpen_WithEmptyString_WhenCircuitOpen()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var options = new RedisCircuitBreakerOptions
        {
            FailureThreshold = 2,
            CooldownDuration = TimeSpan.FromMilliseconds(200),
        };
        var breaker = new RedisCircuitBreaker(options);
        var graph = new ResilientGraphStore(
            new RedisGraphStore(redis, "sluice-graph-dump"),
            breaker
        );

        await container.StopAsync();
        await Task.Delay(500);

        for (int i = 0; i < 2; i++)
        {
            await graph.FindAffectedEntries(
                [new ResourceAddress(ResourceKind.Entity, "user", $"dump-{i}")],
                CancellationToken.None
            );
        }

        breaker.State.Should().Be(BreakerState.Open);

        var dump = await graph.DumpGraphAsync(CancellationToken.None);
        dump.Should().BeEmpty();
    }

    [RequireDockerFact]
    public async Task StampedeCoordinator_FailOpen_WhenCircuitOpen()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var options = new RedisCircuitBreakerOptions
        {
            FailureThreshold = 2,
            CooldownDuration = TimeSpan.FromMilliseconds(200),
        };
        var breaker = new RedisCircuitBreaker(options);
        var stampede = new ResilientStampedeCoordinator(
            new RedisStampedeCoordinator(redis, "sluice-stampede-fail"),
            breaker
        );

        await container.StopAsync();
        await Task.Delay(500);

        for (int i = 0; i < 2; i++)
        {
            await stampede.TryAcquireAsync(
                $"lease-{i}",
                TimeSpan.FromSeconds(30),
                CancellationToken.None
            );
        }

        breaker.State.Should().Be(BreakerState.Open);

        var lease = await stampede.TryAcquireAsync(
            "should-fail",
            TimeSpan.FromSeconds(30),
            CancellationToken.None
        );
        lease.Should().BeNull();

        await stampede.ClearAsync(CancellationToken.None);
    }

    [RequireDockerFact]
    public async Task SluiceRedisCreate_WiresAllComponents()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        var sluice = SluiceRedis.Create(
            redis,
            "sluice-factory-test",
            circuitBreakerOptions: new RedisCircuitBreakerOptions { FailureThreshold = 10 }
        );

        var computeCount = 0;
        var query = new CachedQuery<string, string>(
            "factory-test",
            key => key,
            async (key, scope) =>
            {
                computeCount++;
                return await ValueTask.FromResult($"computed-{key}");
            },
            allowUntracked: true
        );

        var result1 = await sluice.Get(query, "alice", CancellationToken.None);
        result1.Should().Be("computed-alice");
        computeCount.Should().Be(1);

        var result2 = await sluice.Get(query, "alice", CancellationToken.None);
        result2.Should().Be("computed-alice");
        computeCount.Should().Be(1);

        await redis.DisposeAsync();
    }
}
