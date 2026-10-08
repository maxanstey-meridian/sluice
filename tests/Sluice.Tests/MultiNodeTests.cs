using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

public sealed class MultiNodeTests
{
    private static readonly Source<int> _rows = new("cluster-rows");
    private static readonly Query<string, string> _view = new("cluster-view");

    private readonly MemoryDistributedCache _l2 = new(
        Options.Create(new MemoryDistributedCacheOptions())
    );
    private readonly string _bus = Guid.NewGuid().ToString();
    private readonly Dictionary<int, string> _data = new() { [1] = "old" };
    private int _runs;

    private Task<string> View(SluiceCache cache) =>
        cache
            .GetOrSetAsync(
                _view,
                "k",
                (read, _) =>
                {
                    _runs++;
                    read.From(_rows.For(1));
                    return Task.FromResult(_data[1]);
                }
            )
            .AsTask();

    [Fact]
    public async Task An_invalidation_on_one_node_evicts_on_another()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await View(a.Cache);
        (await View(b.Cache)).Should().Be("old");
        _runs.Should().Be(1, "node B reads node A's entry from the shared L2");

        _data[1] = "new";
        await a.Cache.InvalidateAsync(_rows.For(1));

        (await View(b.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    [Theory]
    [InlineData("*", false)]
    [InlineData("!", false)]
    [InlineData("-rem", true)]
    [InlineData("-exp", true)]
    public async Task A_source_named_like_a_clear_sentinel_clears_nothing_on_other_nodes(
        string name,
        bool safeStrings
    )
    {
        void Strings(IFusionCacheBuilder cache)
        {
            if (safeStrings)
            {
                cache.WithOptions(o => o.InternalStrings.SetToSafeStrings());
            }
        }

        await using var a = SluiceNode.Clustered(_l2, _bus, Strings);
        await using var b = SluiceNode.Clustered(_l2, _bus, Strings);
        await View(b.Cache);
        var sentinel = new Source<int>(name);

        await a.Cache.InvalidateAsync([sentinel.All, sentinel.For(1)]);
        await View(b.Cache);

        _runs.Should().Be(1);
    }
}
