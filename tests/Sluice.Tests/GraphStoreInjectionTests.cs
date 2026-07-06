namespace Sluice.Tests;

public sealed class GraphStoreInjectionTests
{
    [Fact]
    public async Task OperationRegistry_InjectedGraphStore_InvalidatesCacheEntry()
    {
        var cacheStore = new InMemoryCacheStore();
        var graphStore = new SpyingGraphStore();
        var registry = new OperationRegistry(cacheStore, graphStore);

        var entryKey = "test-key";
        var entry = new CacheEntry<string>("value", [], DateTimeOffset.UtcNow, null);
        await cacheStore.SetAsync(entryKey, entry, CancellationToken.None);

        graphStore.FindAffectedEntriesResult = [entryKey];

        var address = new ResourceAddress(ResourceKind.Entity, "test", "key");
        await registry.ApplyAsync(
            async ctx => await ctx.Apply(() => Task.CompletedTask, new WriteEffect(address)),
            CancellationToken.None
        );

        graphStore.FindAffectedEntriesCalled.Should().BeTrue();

        var removed = await cacheStore.GetAsync<string>(entryKey, CancellationToken.None);
        removed.Should().BeNull();
    }

    [Fact]
    public async Task SluiceKernel_InjectedGraphStore_ReturnsCustomDump()
    {
        var cacheStore = new InMemoryCacheStore();
        var graphStore = new SpyingGraphStore();
        var sluice = new SluiceKernel(cacheStore, graphStore);

        graphStore.DumpGraphAsyncResult = "sentinel-dump";

        var dump = await sluice.DumpGraphAsync(CancellationToken.None);

        dump.Should().Be("sentinel-dump");
    }

    private sealed class SpyingGraphStore : IGraphStore
    {
        public bool FindAffectedEntriesCalled { get; private set; }
        public IReadOnlyList<string> FindAffectedEntriesResult { get; set; } = [];
        public string DumpGraphAsyncResult { get; set; } = "";

        public Task ClearEntryEdges(string entryKey, CancellationToken ct) => Task.CompletedTask;

        public Task RecordEntry(
            string entryKey,
            IReadOnlyList<ResourceAddress> addresses,
            DateTimeOffset cachedAt,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> FindAffectedEntries(
            IReadOnlyList<ResourceAddress> changedAddresses,
            CancellationToken ct
        )
        {
            FindAffectedEntriesCalled = true;
            return Task.FromResult(FindAffectedEntriesResult);
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string> DumpGraphAsync(CancellationToken ct) =>
            Task.FromResult(DumpGraphAsyncResult);
    }
}
