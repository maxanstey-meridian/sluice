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
