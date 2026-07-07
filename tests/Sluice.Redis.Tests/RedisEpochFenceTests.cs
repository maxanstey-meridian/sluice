using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Sluice.Redis.Tests;

[Collection(RedisTestCollection.Name)]
public sealed class RedisEpochFenceTests
{
    private readonly RedisFixture _fixture;

    public RedisEpochFenceTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _fixture.FlushDatabase();
    }

    [RequireDockerFact]
    public async Task CrossRegistry_Fencing_SelfInvalidatesAfterConcurrentInvalidation()
    {
        var redis = _fixture.Redis;

        var cacheStoreA = new RedisCacheStore(redis, "fence-test");
        var graphStoreA = new RedisGraphStore(redis, "fence-test");
        var stampedeA = new RedisStampedeCoordinator(redis, "fence-test");
        var epochFenceA = new RedisEpochFence(redis, "fence-test");
        var registryA = new OperationRegistry(
            cacheStoreA,
            graphStoreA,
            stampedeCoordinator: stampedeA,
            epochFence: epochFenceA
        );
        var opA = new GatedComputeOp("cross-registry-fence");
        registryA.Register(opA);
        opA.ArmGate();

        var cacheStoreB = new RedisCacheStore(redis, "fence-test");
        var graphStoreB = new RedisGraphStore(redis, "fence-test");
        var stampedeB = new RedisStampedeCoordinator(redis, "fence-test");
        var epochFenceB = new RedisEpochFence(redis, "fence-test");
        var registryB = new OperationRegistry(
            cacheStoreB,
            graphStoreB,
            stampedeCoordinator: stampedeB,
            epochFence: epochFenceB
        );

        var leaderTask = Task.Run(async () =>
            await registryA.ExecuteAsync(opA, "key1", CancellationToken.None)
        );

        await opA.GateEntered;

        await registryB.ApplyAsync(
            ctx =>
                ctx.Apply(
                    () => Task.CompletedTask,
                    new WriteEffect(new ResourceAddress(ResourceKind.Entity, "test", "key1"))
                ),
            CancellationToken.None
        );

        opA.ReleaseGate();

        var result = await leaderTask;
        result.Should().Be("computed-key1");

        var entryKey = $"cross-registry-fence:v1:{JsonSerializer.Serialize("key1")}";
        var stored = await cacheStoreA.GetAsync<string>(entryKey, CancellationToken.None);
        stored.Should().BeNull("the epoch fence should have self-invalidated the stale entry");

        opA.ComputeCount.Should().Be(1);
    }

    [RequireDockerFact]
    public async Task EpochReReadSafety_DetectsOverlap_WhenTrimRemovedRecord()
    {
        var redis = _fixture.Redis;
        var fence = new RedisEpochFence(redis, "reread-guard");

        var overlappingAddr = new ResourceAddress(ResourceKind.Entity, "test", "x");

        await fence.IncrementEpochAsync([overlappingAddr], CancellationToken.None);

        // 256 more invalidations on unrelated resources.
        // At epoch 257, trim removes scores ≤ 257 - 256 = 1,
        // which removes the overlapping record at epoch 1.
        for (int i = 0; i < 256; i++)
        {
            await fence.IncrementEpochAsync(
                [new ResourceAddress(ResourceKind.Entity, "other", i.ToString())],
                CancellationToken.None
            );
        }

        // Without the epoch re-read safety, this would return false
        // because the sorted set range query (1, 1) finds nothing
        // (the record at epoch 1 was trimmed).
        var result = await fence.HasOverlappingInvalidationAsync(
            afterEpoch: 0,
            throughEpoch: 1,
            observedReads: [overlappingAddr],
            CancellationToken.None
        );

        result.Should().BeTrue(
            "epoch re-read safety should detect overlap even when the record was trimmed"
        );
    }

    [RequireDockerFact]
    public async Task NonOverlapping_Invalidation_DoesNotSelfInvalidate()
    {
        var redis = _fixture.Redis;

        var cacheStoreA = new RedisCacheStore(redis, "nonoverlap-test");
        var graphStoreA = new RedisGraphStore(redis, "nonoverlap-test");
        var stampedeA = new RedisStampedeCoordinator(redis, "nonoverlap-test");
        var epochFenceA = new RedisEpochFence(redis, "nonoverlap-test");
        var registryA = new OperationRegistry(
            cacheStoreA,
            graphStoreA,
            stampedeCoordinator: stampedeA,
            epochFence: epochFenceA
        );
        var opA = new GatedComputeOp("nonoverlap-op");
        registryA.Register(opA);
        opA.ArmGate();

        var cacheStoreB = new RedisCacheStore(redis, "nonoverlap-test");
        var graphStoreB = new RedisGraphStore(redis, "nonoverlap-test");
        var stampedeB = new RedisStampedeCoordinator(redis, "nonoverlap-test");
        var epochFenceB = new RedisEpochFence(redis, "nonoverlap-test");
        var registryB = new OperationRegistry(
            cacheStoreB,
            graphStoreB,
            stampedeCoordinator: stampedeB,
            epochFence: epochFenceB
        );

        var leaderTask = Task.Run(async () =>
            await registryA.ExecuteAsync(opA, "key1", CancellationToken.None)
        );

        await opA.GateEntered;

        // Invalidate a DIFFERENT entity (different key, no overlap with opA's observed read)
        await registryB.ApplyAsync(
            ctx =>
                ctx.Apply(
                    () => Task.CompletedTask,
                    new WriteEffect(new ResourceAddress(ResourceKind.Entity, "test", "other-key"))
                ),
            CancellationToken.None
        );

        opA.ReleaseGate();

        var result = await leaderTask;
        result.Should().Be("computed-key1");

        var entryKey = $"nonoverlap-op:v1:{JsonSerializer.Serialize("key1")}";
        var stored = await cacheStoreA.GetAsync<string>(entryKey, CancellationToken.None);
        stored.Should().NotBeNull("non-overlapping invalidation should not cause self-invalidation");
        stored!.Value.Should().Be("computed-key1");

        opA.ComputeCount.Should().Be(1);
    }

    [RequireDockerFact]
    public async Task ConservativeFallback_SelfInvalidatesAfterManyInvalidations()
    {
        var redis = _fixture.Redis;

        var cacheStoreA = new RedisCacheStore(redis, "conservative-test");
        var graphStoreA = new RedisGraphStore(redis, "conservative-test");
        var stampedeA = new RedisStampedeCoordinator(redis, "conservative-test");
        var epochFenceA = new RedisEpochFence(redis, "conservative-test");
        var registryA = new OperationRegistry(
            cacheStoreA,
            graphStoreA,
            stampedeCoordinator: stampedeA,
            epochFence: epochFenceA
        );
        var opA = new GatedComputeOp("conservative-op");
        registryA.Register(opA);
        opA.ArmGate();

        var epochFenceB = new RedisEpochFence(redis, "conservative-test");

        var leaderTask = Task.Run(async () =>
            await registryA.ExecuteAsync(opA, "key1", CancellationToken.None)
        );

        await opA.GateEntered;

        // Advance epoch by 257 on unrelated resources (no address overlap with opA's read)
        for (int i = 0; i < 257; i++)
        {
            await epochFenceB.IncrementEpochAsync(
                [new ResourceAddress(ResourceKind.Entity, "other", i.ToString())],
                CancellationToken.None
            );
        }

        opA.ReleaseGate();

        var result = await leaderTask;
        result.Should().Be("computed-key1");

        var entryKey = $"conservative-op:v1:{JsonSerializer.Serialize("key1")}";
        var stored = await cacheStoreA.GetAsync<string>(entryKey, CancellationToken.None);
        stored.Should().BeNull(
            "conservative fallback should self-invalidate after 257 unrelated epoch advances"
        );

        opA.ComputeCount.Should().Be(1);
    }

    [RequireDockerFact]
    public async Task SluiceRedisCreate_WiresEpochFence_CrossRegistrySelfInvalidation()
    {
        var redis = _fixture.Redis;

        var kernelA = SluiceRedis.Create(redis, "factory-test");
        var kernelB = SluiceRedis.Create(redis, "factory-test");

        var gateEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var gateRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var computeCount = 0;

        var query = new CachedQuery<string, string>(
            "factory-fencing",
            key => key,
            async (key, scope) =>
            {
                var addr = new ResourceAddress(ResourceKind.Entity, "test", key);
                return await scope.Track<string>(addr, async () =>
                {
                    gateEntered.TrySetResult(true);
                    await gateRelease.Task;
                    Interlocked.Increment(ref computeCount);
                    return $"computed-{key}";
                });
            },
            allowUntracked: false
        );

        var getTask = Task.Run(() => kernelA.Get(query, "key1", CancellationToken.None));

        await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await kernelB.Invalidate(
            new WriteEffect(new ResourceAddress(ResourceKind.Entity, "test", "key1")),
            CancellationToken.None
        );

        gateRelease.TrySetResult(true);

        var result = await getTask;
        result.Should().Be("computed-key1");

        var cacheKey = $"factory-test:cache:factory-fencing:v1:{JsonSerializer.Serialize("key1")}";
        var stored = await redis.GetDatabase().StringGetAsync(cacheKey);
        stored.IsNull.Should().BeTrue(
            "epoch fence wired by SluiceRedis.Create should self-invalidate the stale entry"
        );

        computeCount.Should().Be(1);
    }

    private sealed class GatedComputeOp : CachedOperation<string, string>
    {
        public int ComputeCount { get; private set; }
        private TaskCompletionSource<bool>? _gateEntered;
        private TaskCompletionSource<bool>? _gateRelease;

        public GatedComputeOp(string name)
            : base(name, 1, allowUntracked: false) { }

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
            ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "test", key));

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
