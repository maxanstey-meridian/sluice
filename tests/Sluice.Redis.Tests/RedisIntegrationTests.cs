using FluentAssertions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Sluice.Redis.Tests;

public sealed class RedisIntegrationTests
{
    [RequireDockerFact]
    public async Task CacheMiss_ComputesAndStoresInRedis()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("test-cache-miss");
        registry.Register(op);
        var result = await registry.ExecuteAsync(op, "key1", CancellationToken.None);
        result.Should().Be("computed-key1");

        var entryKey = EntryKey("test-cache-miss", 1, "key1");
        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().NotBeNull();
        cached!.Value.Should().Be("computed-key1");
    }

    [RequireDockerFact]
    public async Task CacheHit_ReadsFromRedis()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("test-cache-hit");
        registry.Register(op);
        var result1 = await registry.ExecuteAsync(op, "hit-key", CancellationToken.None);
        result1.Should().Be("computed-hit-key");
        op.ComputeCount.Should().Be(1);

        var result2 = await registry.ExecuteAsync(op, "hit-key", CancellationToken.None);
        result2.Should().Be("computed-hit-key");
        op.ComputeCount.Should().Be(1);

        var entryKey = EntryKey("test-cache-hit", 1, "hit-key");
        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().NotBeNull();
    }

    [RequireDockerFact]
    public async Task Write_InvalidatesCacheEntry()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("test-write-invalidate");
        registry.Register(op);

        var result1 = await registry.ExecuteAsync(op, "alice", CancellationToken.None);
        result1.Should().Be("computed-alice");

        var address = new ResourceAddress(ResourceKind.Entity, "user", "alice");
        await registry.ApplyAsync(
            ctx => ctx.Apply(() => Task.CompletedTask, new WriteEffect(address)),
            CancellationToken.None
        );

        var entryKey = EntryKey("test-write-invalidate", 1, "alice");
        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().BeNull();
    }

    [RequireDockerFact]
    public async Task Wildcard_Invalidation_Works()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op1 = new ComputeOp("op1");
        var op2 = new ComputeOp("op2");
        registry.Register(op1).Register(op2);

        var r1 = await registry.ExecuteAsync(op1, "alice", CancellationToken.None);
        var r2 = await registry.ExecuteAsync(op2, "bob", CancellationToken.None);
        r1.Should().Be("computed-alice");
        r2.Should().Be("computed-bob");

        var wildcard = new ResourceAddress(ResourceKind.Entity, "user", "*");
        await registry.ApplyAsync(
            ctx => ctx.Apply(() => Task.CompletedTask, new WriteEffect(wildcard)),
            CancellationToken.None
        );

        var ek1 = EntryKey("op1", 1, "alice");
        var ek2 = EntryKey("op2", 1, "bob");
        var v1 = await cacheStore.GetAsync<string>(ek1, CancellationToken.None);
        var v2 = await cacheStore.GetAsync<string>(ek2, CancellationToken.None);
        v1.Should().BeNull();
        v2.Should().BeNull();
    }

    [RequireDockerFact]
    public async Task Ttl_Expiry_Evicts()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("ttl-test", ttl: TimeSpan.FromSeconds(1));
        registry.Register(op);

        var result = await registry.ExecuteAsync(op, "ttl-key", CancellationToken.None);
        result.Should().Be("computed-ttl-key");

        await Task.Delay(2000);

        var entryKey = EntryKey("ttl-test", 1, "ttl-key");
        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().BeNull();
    }

    [RequireDockerFact]
    public async Task FlushAllAsync_ClearsBothStores()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("flush-test");
        registry.Register(op);

        await registry.ExecuteAsync(op, "flush-key", CancellationToken.None);

        await registry.FlushAllAsync(CancellationToken.None);

        var entryKey = EntryKey("flush-test", 1, "flush-key");
        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().BeNull();

        var address = new ResourceAddress(ResourceKind.Entity, "test", "flush-key");
        var affected = await graphStore.FindAffectedEntries([address], CancellationToken.None);
        affected.Should().BeEmpty();
    }

    private static string EntryKey(string name, int version, string key) =>
        $"{name}:v{version}:{CacheKey.From(key).Value}";

    private sealed class ComputeOp : CachedOperation<string, string>
    {
        public int ComputeCount { get; private set; }

        public ComputeOp(string name, TimeSpan? ttl = null)
            : base(name, 1, ttl) { }

        protected override CacheKey Key(string key) => CacheKey.From(key);

        protected override ValueTask<string> Compute(string key, OperationContext ctx)
        {
            ComputeCount++;
            ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "user", key));
            return new ValueTask<string>($"computed-{key}");
        }
    }
}
