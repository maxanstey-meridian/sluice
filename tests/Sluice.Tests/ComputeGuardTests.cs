using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

public sealed class ComputeGuardTests
{
    private static readonly Source<int> _rows = new("guard-rows");
    private static readonly Query<string, string> _outer = new("guard-outer");
    private static readonly Query<string, string> _inner = new("guard-inner");

    private readonly MemoryDistributedCache _l2 = new(
        Options.Create(new MemoryDistributedCacheOptions())
    );
    private readonly string _bus = Guid.NewGuid().ToString();
    private int _runs;

    private Task<string> Good(SluiceReads read, CancellationToken ct)
    {
        _runs++;
        read.From(_rows.For(1));
        return Task.FromResult($"good#{_runs}");
    }

    private Task<string> NoReads(SluiceReads read, CancellationToken ct)
    {
        _runs++;
        return Task.FromResult("unrecorded");
    }

    // The nested call throws; this compute swallows it and returns normally, which must not help.
    private Func<SluiceReads, CancellationToken, Task<string>> Nesting(SluiceCache cache) =>
        async (read, ct) =>
        {
            _runs++;
            read.From(_rows.For(1));
            try
            {
                await cache.GetOrSetAsync(_inner, "x", Good, ct);
            }
            catch (InvalidOperationException) { }

            return "nested";
        };

    [Fact]
    public async Task A_compute_that_records_no_reads_throws_and_caches_nothing()
    {
        await using var node = new SluiceNode();

        var call = () => node.Cache.GetOrSetAsync(_outer, "k", NoReads).AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*use IFusionCache directly*");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
    }

    [Fact]
    public async Task A_compute_that_records_no_reads_stores_nothing_another_node_can_read()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);

        var call = () => a.Cache.GetOrSetAsync(_outer, "k", NoReads).AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>();
        (await b.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
    }

    [Fact]
    public async Task A_compute_that_records_no_reads_evicts_nothing_on_another_node()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await b.Cache.GetOrSetAsync(_outer, "k", Good);

        // Node A must miss to run its compute: drop the entry from L2 without telling node B. That also means
        // node B, told of a newer entry, finds none in L2 and drops its own, which is what this test detects.
        await a.Fusion.RemoveAsync(
            "guard-outer:k",
            new FusionCacheEntryOptions { SkipBackplaneNotifications = true }
        );
        var call = () => a.Cache.GetOrSetAsync(_outer, "k", NoReads).AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>();
        (await b.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
    }

    [Fact]
    public async Task Calling_GetOrSetAsync_inside_a_compute_throws()
    {
        await using var node = new SluiceNode();

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _outer,
                    "k",
                    async (read, ct) =>
                    {
                        read.From(_rows.For(1));
                        return await node.Cache.GetOrSetAsync(_inner, "x", Good, ct);
                    }
                )
                .AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*inside another compute*");
        _runs.Should().Be(0, "the inner compute must never run");
    }

    [Fact]
    public async Task The_zero_reads_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry()
    {
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o => o.IsFailSafeEnabled = true)
        );
        await node.Cache.GetOrSetAsync(_outer, "k", Good);
        await node.Fusion.ExpireAsync("guard-outer:k");

        var call = () => node.Cache.GetOrSetAsync(_outer, "k", NoReads).AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*recorded no dependencies*");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#3");
    }

    [Fact]
    public async Task The_nesting_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry()
    {
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o => o.IsFailSafeEnabled = true)
        );
        await node.Cache.GetOrSetAsync(_outer, "k", Good);
        await node.Fusion.ExpireAsync("guard-outer:k");

        var call = () => node.Cache.GetOrSetAsync(_outer, "k", Nesting(node.Cache)).AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*inside another compute*");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#3");
    }
}
