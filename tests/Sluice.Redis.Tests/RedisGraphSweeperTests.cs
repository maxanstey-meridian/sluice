using FluentAssertions;
using Xunit;

namespace Sluice.Redis.Tests;

[Collection(RedisTestCollection.Name)]
public sealed class RedisGraphSweeperTests
{
    private readonly RedisFixture _fixture;

    public RedisGraphSweeperTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _fixture.FlushDatabase();
    }

    [RequireDockerFact]
    public async Task Sweep_Removes_Orphaned_Edges()
    {
        var redis = _fixture.Redis;

        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("sweep-orphan");
        registry.Register(op);

        await registry.ExecuteAsync(op, "alice", CancellationToken.None);

        var entryKey = EntryKey("sweep-orphan", 1, "alice");

        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().NotBeNull();

        var db = redis.GetDatabase();
        await db.KeyDeleteAsync($"sluice:cache:{entryKey}");

        cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().BeNull();

        var address = new ResourceAddress(ResourceKind.Entity, "user", "alice");
        var affected = await graphStore.FindAffectedEntries([address], CancellationToken.None);
        affected.Should().NotBeEmpty();

        var sweeper = new RedisGraphSweeper(redis, graphStore);
        var removed = await sweeper.SweepAsync(100, CancellationToken.None);

        removed.Should().Be(1);

        affected = await graphStore.FindAffectedEntries([address], CancellationToken.None);
        affected.Should().BeEmpty();
    }

    [RequireDockerFact]
    public async Task Sweep_Preserves_Live_Entries()
    {
        var redis = _fixture.Redis;

        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("sweep-live");
        registry.Register(op);

        await registry.ExecuteAsync(op, "bob", CancellationToken.None);

        var sweeper = new RedisGraphSweeper(redis, graphStore);
        var removed = await sweeper.SweepAsync(100, CancellationToken.None);

        removed.Should().Be(0);

        var entryKey = EntryKey("sweep-live", 1, "bob");
        var cached = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        cached.Should().NotBeNull();

        var address = new ResourceAddress(ResourceKind.Entity, "user", "bob");
        var affected = await graphStore.FindAffectedEntries([address], CancellationToken.None);
        affected.Should().NotBeEmpty();
    }

    [RequireDockerFact]
    public async Task Sweep_On_Empty_Graph_Returns_Zero()
    {
        var redis = _fixture.Redis;

        var graphStore = new RedisGraphStore(redis);
        var sweeper = new RedisGraphSweeper(redis, graphStore);

        var removed = await sweeper.SweepAsync(100, CancellationToken.None);
        removed.Should().Be(0);
    }

    [RequireDockerFact]
    public async Task Sweep_Removes_Only_Orphaned_Edges()
    {
        var redis = _fixture.Redis;

        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("sweep-mixed");
        registry.Register(op);

        await registry.ExecuteAsync(op, "alice", CancellationToken.None);
        await registry.ExecuteAsync(op, "bob", CancellationToken.None);
        await registry.ExecuteAsync(op, "carol", CancellationToken.None);

        var db = redis.GetDatabase();
        await db.KeyDeleteAsync($"sluice:cache:{EntryKey("sweep-mixed", 1, "alice")}");
        await db.KeyDeleteAsync($"sluice:cache:{EntryKey("sweep-mixed", 1, "carol")}");

        var sweeper = new RedisGraphSweeper(redis, graphStore);
        var removed = await sweeper.SweepAsync(100, CancellationToken.None);

        removed.Should().Be(2);

        var bobAddress = new ResourceAddress(ResourceKind.Entity, "user", "bob");
        var bobAffected = await graphStore.FindAffectedEntries(
            [bobAddress],
            CancellationToken.None
        );
        bobAffected.Should().NotBeEmpty();

        var aliceAddress = new ResourceAddress(ResourceKind.Entity, "user", "alice");
        var aliceAffected = await graphStore.FindAffectedEntries(
            [aliceAddress],
            CancellationToken.None
        );
        aliceAffected.Should().BeEmpty();

        var carolAddress = new ResourceAddress(ResourceKind.Entity, "user", "carol");
        var carolAffected = await graphStore.FindAffectedEntries(
            [carolAddress],
            CancellationToken.None
        );
        carolAffected.Should().BeEmpty();
    }

    [RequireDockerFact]
    public async Task Sweep_CleansUp_Fwd_And_Ts_Keys()
    {
        var redis = _fixture.Redis;

        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var registry = new OperationRegistry(cacheStore, graphStore);
        var op = new ComputeOp("sweep-fwd-ts");
        registry.Register(op);

        await registry.ExecuteAsync(op, "dave", CancellationToken.None);

        var entryKey = EntryKey("sweep-fwd-ts", 1, "dave");

        var db = redis.GetDatabase();
        await db.KeyDeleteAsync($"sluice:cache:{entryKey}");

        var fwdExistsBefore = await db.KeyExistsAsync($"sluice:fwd:{entryKey}");
        var tsExistsBefore = await db.KeyExistsAsync($"sluice:ts:{entryKey}");
        fwdExistsBefore.Should().BeTrue();
        tsExistsBefore.Should().BeTrue();

        var sweeper = new RedisGraphSweeper(redis, graphStore);
        await sweeper.SweepAsync(100, CancellationToken.None);

        var fwdExistsAfter = await db.KeyExistsAsync($"sluice:fwd:{entryKey}");
        var tsExistsAfter = await db.KeyExistsAsync($"sluice:ts:{entryKey}");
        fwdExistsAfter.Should().BeFalse();
        tsExistsAfter.Should().BeFalse();
    }

    private static string EntryKey(string name, int version, string key) =>
        $"{name}:v{version}:{CacheKey.From(key).Value}";

    private sealed class ComputeOp : CachedOperation<string, string>
    {
        public ComputeOp(string name, TimeSpan? ttl = null)
            : base(name, 1, ttl) { }

        protected override CacheKey Key(string key) => CacheKey.From(key);

        protected override ValueTask<string> Compute(string key, OperationContext ctx)
        {
            ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "user", key));
            return new ValueTask<string>($"computed-{key}");
        }
    }
}
