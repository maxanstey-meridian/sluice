using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sluice.Infrastructure;
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
            .WithMessage("*register one with services.AddFusionCache() and inject IFusionCache*");
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
    public async Task Calling_GetOrSetAsync_inside_a_fused_fetch_throws()
    {
        await using var node = new SluiceNode();

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _outer,
                    "k",
                    (read, ct) =>
                        read.From(
                            _rows.For(1),
                            fetchCt => node.Cache.GetOrSetAsync(_inner, "x", Good, fetchCt).AsTask()
                        )
                )
                .AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*'guard-inner'*inside another compute*");
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

    private sealed class Resource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Nested computes are refused, so a read can only reach another compute by being captured: compute A's read,
    // used in compute B while A is still running. B catches the exception, which must not help.
    [Theory]
    [InlineData("records")]
    [InlineData("fetches")]
    [InlineData("holds")]
    [InlineData("leaves uncached")]
    public async Task Using_another_running_compute_s_read_throws_and_caches_nothing(string use)
    {
        await using var node = new SluiceNode();
        SluiceReads? captured = null;
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aMayFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fetched = false;

        var a = node
            .Cache.GetOrSetAsync(
                _outer,
                "a",
                async (read, _) =>
                {
                    read.From(_rows.For(1));
                    captured = read;
                    aStarted.SetResult();
                    await aMayFinish.Task;
                    return "a";
                }
            )
            .AsTask();
        await aStarted.Task;

        var b = () =>
            node
                .Cache.GetOrSetAsync(
                    _outer,
                    "b",
                    async (read, _) =>
                    {
                        read.From(_rows.For(2));
                        try
                        {
                            switch (use)
                            {
                                case "records":
                                    captured!.From(_rows.For(3));
                                    break;
                                case "fetches":
                                    await captured!.From(
                                        _rows.For(3),
                                        _ => Task.FromResult(fetched = true)
                                    );
                                    break;
                                case "leaves uncached":
                                    captured!.DoNotCache();
                                    break;
                                default:
                                    ActiveCompute.Hold(captured!, _ => new Resource());
                                    break;
                            }
                        }
                        catch (InvalidOperationException) { }

                        return "b";
                    }
                )
                .AsTask();

        await b.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "The compute of query guard-outer used the read of another compute (query guard-outer), so what it "
                    + "reads wouldn't be recorded on this one. Use the read the compute was given."
            );
        fetched.Should().BeFalse();
        aMayFinish.SetResult();
        (await a).Should().Be("a");
        (await node.Cache.GetOrSetAsync(_outer, "a", Good))
            .Should()
            .Be("a", "the other compute's use changed nothing on this one");
        (await node.Cache.GetOrSetAsync(_outer, "b", Good)).Should().Be("good#1");
    }

    // Before the core checked, a returned compute's read threw only "called after the compute returned", which the
    // running compute could catch and then cache without what it read.
    [Fact]
    public async Task Using_a_returned_compute_s_read_throws_and_caches_nothing()
    {
        await using var node = new SluiceNode();
        SluiceReads? captured = null;
        await node.Cache.GetOrSetAsync(
            _outer,
            "returned",
            (read, _) =>
            {
                read.From(_rows.For(1));
                captured = read;
                return Task.FromResult("a");
            }
        );

        var b = () =>
            node
                .Cache.GetOrSetAsync(
                    _outer,
                    "b",
                    (read, _) =>
                    {
                        read.From(_rows.For(2));
                        try
                        {
                            captured!.From(_rows.For(3));
                        }
                        catch (InvalidOperationException) { }

                        return Task.FromResult("b");
                    }
                )
                .AsTask();

        await b.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*which has already returned*");
        (await node.Cache.GetOrSetAsync(_outer, "b", Good)).Should().Be("good#1");
    }
}
