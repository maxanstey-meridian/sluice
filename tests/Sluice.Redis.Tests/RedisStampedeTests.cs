using FluentAssertions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Sluice.Redis.Tests;

public sealed class RedisStampedeTests
{
    [RequireDockerFact]
    public async Task Stampede_CrossRegistry_DeduplicatesViaRedis()
    {
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

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

        // Arm gate on A before starting tasks to ensure A enters Compute and blocks
        var tasks = new List<Task<string>>
        {
            Task.Run(async () => await registryA.ExecuteAsync(opA, "key1", CancellationToken.None)),
            Task.Run(async () => await registryB.ExecuteAsync(opB, "key1", CancellationToken.None)),
        };

        // Wait for the leader to be blocked in Compute (lease acquired, gate armed)
        await opA.GateEntered;

        // At this point: A has the lease and is blocked. B should be polling or about to compute.
        // Give B a brief moment to enter its polling path, then check counts.
        await Task.Delay(100);
        opA.ComputeCount.Should().Be(0, "leader should be blocked on gate before computing");
        opB.ComputeCount.Should().Be(0, "follower should be polling, not computing");

        // Release the gate on registry A so it can compute and write the entry
        opA.ReleaseGate();

        var results = await Task.WhenAll(tasks);

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
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

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
        await using var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var db = redis.GetDatabase();

        // Pre-acquire the Redis lock for the entry key so no one else can
        await db.StringSetAsync(
            "sluice:lock:wait-timeout-test",
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

        // Clean up the held lock
        await db.KeyDeleteAsync("sluice:lock:wait-timeout-test");
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

        protected override ValueTask<string> Compute(string key, OperationContext ctx)
        {
            _gateEntered?.TrySetResult(true);
            var release = _gateRelease;
            if (release is not null)
            {
                release.Task.Wait();
            }
            ComputeCount++;
            return new ValueTask<string>($"computed-{key}");
        }
    }
}
