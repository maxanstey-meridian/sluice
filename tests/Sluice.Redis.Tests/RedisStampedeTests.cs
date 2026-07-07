using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Sluice.Redis.Tests;

[Collection(RedisTestCollection.Name)]
public sealed class RedisStampedeTests
{
    private readonly RedisFixture _fixture;

    public RedisStampedeTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _fixture.FlushDatabase();
    }

    [RequireDockerFact]
    public async Task Stampede_CrossRegistry_DeduplicatesViaRedis()
    {
        var redis = _fixture.Redis;

        var cacheStoreA = new RedisCacheStore(redis);
        var graphStoreA = new RedisGraphStore(redis);
        var stampedeA = new RedisStampedeCoordinator(redis);
        var registryA = new OperationRegistry(
            cacheStoreA,
            graphStoreA,
            stampedeCoordinator: stampedeA
        );
        var opA = new GatedComputeOp("cross-registry-dedup");
        registryA.Register(opA);
        opA.ArmGate();

        var cacheStoreB = new RedisCacheStore(redis);
        var graphStoreB = new RedisGraphStore(redis);
        var stampedeB = new RedisStampedeCoordinator(redis);
        var registryB = new OperationRegistry(
            cacheStoreB,
            graphStoreB,
            stampedeCoordinator: stampedeB
        );
        var opB = new GatedComputeOp("cross-registry-dedup");
        registryB.Register(opB);

        var leaderTask = Task.Run(async () =>
            await registryA.ExecuteAsync(opA, "key1", CancellationToken.None)
        );

        await opA.GateEntered;

        var followerTask = Task.Run(async () =>
            await registryB.ExecuteAsync(opB, "key1", CancellationToken.None)
        );

        await Task.Delay(100);
        opA.ComputeCount.Should().Be(0, "leader should be blocked on gate before computing");
        opB.ComputeCount.Should().Be(0, "follower should be polling, not computing");

        opA.ReleaseGate();

        var results = await Task.WhenAll(leaderTask, followerTask);

        results[0].Should().Be("computed-key1");
        results[1].Should().Be("computed-key1");

        // Only one registry should have computed (the leader)
        // The follower sees the entry via cache polling
        (opA.ComputeCount + opB.ComputeCount)
            .Should()
            .Be(1);
    }

    [RequireDockerFact]
    public async Task Stampede_FollowerPollsCacheAndReturns()
    {
        var redis = _fixture.Redis;

        // Registry A — leader
        var cacheStoreA = new RedisCacheStore(redis);
        var graphStoreA = new RedisGraphStore(redis);
        var stampedeA = new RedisStampedeCoordinator(redis);
        var registryA = new OperationRegistry(
            cacheStoreA,
            graphStoreA,
            stampedeCoordinator: stampedeA
        );
        var opA = new GatedComputeOp("follower-poll");
        registryA.Register(opA);
        opA.ArmGate();

        // Registry B — follower
        var cacheStoreB = new RedisCacheStore(redis);
        var graphStoreB = new RedisGraphStore(redis);
        var stampedeB = new RedisStampedeCoordinator(redis);
        var registryB = new OperationRegistry(
            cacheStoreB,
            graphStoreB,
            stampedeCoordinator: stampedeB,
            stampedeOptions: new StampedeOptions { WaitTimeout = TimeSpan.FromSeconds(3) }
        );
        var opB = new GatedComputeOp("follower-poll");
        registryB.Register(opB);

        // Start leader first
        var leaderTask = Task.Run(async () =>
            await registryA.ExecuteAsync(opA, "poll-key", CancellationToken.None)
        );

        // Wait for leader to enter Compute (lease acquired, blocked on gate)
        await opA.GateEntered;

        // Start follower — it will miss cache, fail to acquire lease, poll, and find the entry after leader releases
        var followerTask = Task.Run(async () =>
            await registryB.ExecuteAsync(opB, "poll-key", CancellationToken.None)
        );

        await Task.Delay(100);
        opA.ComputeCount.Should().Be(0);
        opB.ComputeCount.Should().Be(0);

        // Release leader
        opA.ReleaseGate();

        var results = await Task.WhenAll(leaderTask, followerTask);
        var leaderResult = results[0];
        var followerResult = results[1];

        leaderResult.Should().Be("computed-poll-key");
        followerResult.Should().Be("computed-poll-key");

        // Only the leader should compute; follower should have polled the cache
        opA.ComputeCount.Should().Be(1);
        opB.ComputeCount.Should().Be(0);
    }

    [RequireDockerFact]
    public async Task Stampede_WaitTimeout_ComputesWithoutLease()
    {
        var redis = _fixture.Redis;
        var db = redis.GetDatabase();

        var entryKey = "wait-timeout-fallback:v1:wait-timeout-test";
        await db.StringSetAsync(
            $"sluice:lock:{entryKey}",
            "holder-token",
            TimeSpan.FromSeconds(10),
            When.NotExists
        );

        var cacheStore = new RedisCacheStore(redis);
        var graphStore = new RedisGraphStore(redis);
        var stampede = new RedisStampedeCoordinator(redis);
        var registry = new OperationRegistry(
            cacheStore,
            graphStore,
            stampedeCoordinator: stampede,
            stampedeOptions: new StampedeOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(500),
                MaxBackoff = TimeSpan.FromMilliseconds(50),
            }
        );
        var op = new GatedComputeOp("wait-timeout-fallback");
        registry.Register(op);

        var result = await registry.ExecuteAsync(op, "wait-timeout-test", CancellationToken.None);

        result.Should().Be("computed-wait-timeout-test");
        op.ComputeCount.Should().Be(1);

        await db.KeyDeleteAsync($"sluice:lock:{entryKey}");
    }

    [RequireDockerFact]
    public async Task ClearAsync_RemovesAllLockKeys()
    {
        var redis = _fixture.Redis;
        var db = redis.GetDatabase();

        var stampede = new RedisStampedeCoordinator(redis);

        await db.StringSetAsync(
            "sluice:lock:op-a:v1:key1",
            "token1",
            TimeSpan.FromMinutes(5),
            When.NotExists
        );
        await db.StringSetAsync(
            "sluice:lock:op-b:v1:key2",
            "token2",
            TimeSpan.FromMinutes(5),
            When.NotExists
        );
        await db.StringSetAsync(
            "sluice:lock:op-c:v1:key3",
            "token3",
            TimeSpan.FromMinutes(5),
            When.NotExists
        );

        await stampede.ClearAsync(CancellationToken.None);

        var cursor = 0L;
        var remaining = new List<string>();
        do
        {
            var result = await db.ExecuteAsync(
                "SCAN",
                cursor.ToString(),
                "MATCH",
                "sluice:lock:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            cursor = long.Parse((string?)inner[0] ?? "0");
            foreach (var item in (RedisResult[])inner[1]!)
            {
                var keyStr = (string?)item;
                if (keyStr is not null)
                {
                    remaining.Add(keyStr);
                }
            }
        } while (cursor != 0);

        remaining.Should().BeEmpty();
    }

    private sealed class GatedComputeOp : CachedOperation<string, string>
    {
        public int ComputeCount { get; private set; }
        private TaskCompletionSource<bool>? _gateEntered;
        private TaskCompletionSource<bool>? _gateRelease;

        public GatedComputeOp(string name)
            : base(name, 1, allowUntracked: true) { }

        public Task GateEntered => _gateEntered?.Task ?? Task.CompletedTask;

        public void ArmGate()
        {
            _gateEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _gateRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        public void ReleaseGate() => _gateRelease?.TrySetResult(true);

        protected override CacheKey Key(string key) => CacheKey.From(key);

        protected override async ValueTask<string> Compute(string key, OperationContext ctx)
        {
            _gateEntered?.TrySetResult(true);
            var release = _gateRelease;
            if (release is not null)
            {
                await release.Task;
            }
            ComputeCount++;
            return $"computed-{key}";
        }
    }
}
