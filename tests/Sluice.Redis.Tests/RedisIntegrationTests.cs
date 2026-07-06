using FluentAssertions;
using Xunit;

namespace Sluice.Redis.Tests;

[Collection(RedisTestCollection.Name)]
public sealed class RedisIntegrationTests
{
    private readonly RedisFixture _fixture;

    public RedisIntegrationTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _fixture.FlushDatabase();
    }

    [RequireDockerFact]
    public async Task CacheMiss_ComputesAndStoresInRedis()
    {
        var redis = _fixture.Redis;
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
        var redis = _fixture.Redis;
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
        var redis = _fixture.Redis;
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
        var redis = _fixture.Redis;
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
        var redis = _fixture.Redis;
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
        var redis = _fixture.Redis;
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

    private sealed record NestedItem(Guid Id, string Label, DateTimeOffset CreatedAt);

    private sealed record ComplexDto(
        Guid Id,
        string Name,
        DateTimeOffset Timestamp,
        NestedItem Primary,
        List<NestedItem> Items
    );

    [RequireDockerFact]
    public async Task Dto_RoundTrips_ThroughRedis()
    {
        var redis = _fixture.Redis;
        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new CachedDtoOp("test-dto-roundtrip");
        registry.Register(op);

        var result1 = await registry.ExecuteAsync(op, "key1", CancellationToken.None);
        result1.Should().NotBeNull();
        result1!.Id.Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        result1.Name.Should().Be("test-dto");
        result1.Timestamp.Should().Be(new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));
        result1.Primary.Label.Should().Be("primary-item");
        result1
            .Primary.CreatedAt.Should()
            .Be(new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero));
        result1.Items.Should().HaveCount(2);
        result1.Items[0].Label.Should().Be("item-a");
        result1.Items[1].Label.Should().Be("item-b");

        var result2 = await registry.ExecuteAsync(op, "key1", CancellationToken.None);
        result2.Should().BeEquivalentTo(result1);

        var entryKey = EntryKey("test-dto-roundtrip", 1, "key1");
        var cached = await cacheStore.GetAsync<ComplexDto>(entryKey, CancellationToken.None);
        cached.Should().NotBeNull();
        cached!.Value.Id.Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        cached.Value.Name.Should().Be("test-dto");
        cached.Value.Timestamp.Should().Be(new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));
        cached.Value.Primary.Id.Should().Be(op.TestPrimary.Id);
        cached.Value.Primary.Label.Should().Be("primary-item");
        cached
            .Value.Primary.CreatedAt.Should()
            .Be(new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero));
        cached.Value.Items.Should().HaveCount(2);
        cached.Value.Items[0].Label.Should().Be("item-a");
        cached.Value.Items[1].Label.Should().Be("item-b");
        cached.ObservedReads.Should().HaveCount(3);
        cached.ObservedReads[0].Kind.Should().Be(ResourceKind.Entity);
        cached.ObservedReads[0].Name.Should().Be("user");
        cached.ObservedReads[0].Key.Should().Be("key1");
        cached.ObservedReads[1].Kind.Should().Be(ResourceKind.Collection);
        cached.ObservedReads[1].Name.Should().Be("orders");
        cached.ObservedReads[1].Key.Should().Be("customer-1");
        cached.ObservedReads[2].Kind.Should().Be(ResourceKind.External);
        cached.ObservedReads[2].Name.Should().Be("stripe");
        cached.ObservedReads[2].Key.Should().Be("cus_abc");
    }

    private sealed class CachedDtoOp : CachedOperation<string, ComplexDto>
    {
        public NestedItem TestPrimary { get; }

        public CachedDtoOp(string name)
            : base(name, 1)
        {
            TestPrimary = new NestedItem(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                "primary-item",
                new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero)
            );
        }

        protected override CacheKey Key(string key) => CacheKey.From(key);

        protected override ValueTask<ComplexDto> Compute(string key, OperationContext ctx)
        {
            ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "user", key));
            ctx.RecordRead(new ResourceAddress(ResourceKind.Collection, "orders", "customer-1"));
            ctx.RecordRead(new ResourceAddress(ResourceKind.External, "stripe", "cus_abc"));
            return new ValueTask<ComplexDto>(
                new ComplexDto(
                    Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    "test-dto",
                    new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero),
                    TestPrimary,
                    new List<NestedItem>
                    {
                        new(
                            Guid.NewGuid(),
                            "item-a",
                            DateTimeOffset.Parse("2025-01-01T00:00:00+00:00")
                        ),
                        new(
                            Guid.NewGuid(),
                            "item-b",
                            DateTimeOffset.Parse("2025-02-01T00:00:00+00:00")
                        ),
                    }
                )
            );
        }
    }

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
