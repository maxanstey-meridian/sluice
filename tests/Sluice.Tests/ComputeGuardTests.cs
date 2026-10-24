using System.Transactions;
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

    private Task<string> Uncached(SluiceReads read, CancellationToken ct)
    {
        _runs++;
        read.From(_rows.For(1));
        read.DoNotCache();
        return Task.FromResult("uncached");
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
    public async Task An_uncached_value_is_stored_nowhere_another_node_can_read()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);

        (await a.Cache.GetOrSetAsync(_outer, "k", Uncached)).Should().Be("uncached");
        (await b.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
    }

    [Fact]
    public async Task An_uncached_value_evicts_nothing_on_another_node()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await b.Cache.GetOrSetAsync(_outer, "k", Good);

        // As in the no-reads case: node A misses, and a backplane notice would make node B drop its own entry.
        await a.Fusion.RemoveAsync(
            "guard-outer:k",
            new FusionCacheEntryOptions { SkipBackplaneNotifications = true }
        );

        (await a.Cache.GetOrSetAsync(_outer, "k", Uncached)).Should().Be("uncached");
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
    [InlineData("adds a step before the store")]
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
                                case "adds a step before the store":
                                    ActiveCompute.BeforeStore(captured!, _ => Task.CompletedTask);
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
                "The compute of query guard-outer used the read of another compute (query guard-outer), so it would "
                    + "act on that compute instead of this one. Use the read the compute was given."
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

    // A probe stands in for a package's transaction: what it has written and not yet committed.
    private static Func<IReadOnlyCollection<Dependency>> Pending(params Dependency[] written) =>
        () => written;

    private const string Uncommitted =
        "Query 'guard-outer' read *, which the caller's own transaction has written and not yet committed. The "
        + "compute reads only what's committed, so the caller would get the data without its own writes. Read those "
        + "rows on the transaction's own context, or after it commits.";

    [Fact]
    public async Task Without_a_flow_box_a_probe_is_not_kept()
    {
        await using var node = new SluiceNode();
        var asked = false;

        FlowWrites
            .AddProbe(
                node.Cache,
                () =>
                {
                    asked = true;
                    return [_rows.For(1)];
                }
            )
            .Should()
            .BeFalse();

        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task A_read_of_the_flow_s_uncommitted_writes_throws_caches_nothing_and_after_the_flow_caches_as_usual()
    {
        await using var node = new SluiceNode();

        using (SluiceFlow.Begin())
        {
            FlowWrites.AddProbe(node.Cache, Pending(_rows.For(1))).Should().BeTrue();
            var call = () => node.Cache.GetOrSetAsync(_outer, "k", Good).AsTask();

            await call.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(Uncommitted.Replace("*", "guard-rows:1"));
        }

        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
    }

    // The fresh value isn't the one cached before, and leaves it in place.
    [Fact]
    public async Task With_unrelated_uncommitted_writes_a_read_computes_fresh_and_stores_nothing()
    {
        await using var node = new SluiceNode();
        await node.Cache.GetOrSetAsync(_outer, "k", Good);

        using (SluiceFlow.Begin())
        {
            FlowWrites.AddProbe(node.Cache, Pending(_rows.For(2)));

            (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
            (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#3");
        }

        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
    }

    // The fresh compute runs as a miss does: a step that records the written row is checked like the compute's own
    // reads.
    [Theory]
    [InlineData("records no dependencies")]
    [InlineData("leaves uncached")]
    [InlineData("records the written row in a step")]
    [InlineData("leaves uncached and records the written row in a step")]
    public async Task With_uncommitted_writes_a_read_keeps_its_guards_and_steps(string compute)
    {
        await using var node = new SluiceNode();
        using var flow = SluiceFlow.Begin();
        FlowWrites.AddProbe(node.Cache, Pending(_rows.For(2)));

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _outer,
                    "k",
                    (read, ct) =>
                    {
                        switch (compute)
                        {
                            case "leaves uncached":
                                read.DoNotCache();
                                break;
                            case "leaves uncached and records the written row in a step":
                                read.DoNotCache();
                                goto case "records the written row in a step";
                            case "records the written row in a step":
                                read.From(_rows.For(1));
                                ActiveCompute.BeforeStore(
                                    read,
                                    _ =>
                                    {
                                        read.From(_rows.For(2));
                                        return Task.CompletedTask;
                                    }
                                );
                                break;
                        }

                        return Task.FromResult("value");
                    }
                )
                .AsTask();

        switch (compute)
        {
            case "records no dependencies":
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("The compute recorded no dependencies*");
                break;
            case "leaves uncached":
                (await call()).Should().Be("value");
                break;
            default:
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage(Uncommitted.Replace("*", "guard-rows:2"));
                break;
        }
    }

    [Fact]
    public async Task A_probe_with_nothing_pending_leaves_reads_cached_as_usual()
    {
        await using var node = new SluiceNode();

        using (SluiceFlow.Begin())
        {
            FlowWrites.AddProbe(node.Cache, Pending());

            (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
            (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
        }
    }

    [Theory]
    [InlineData("a key", "the whole source")]
    [InlineData("the whole source", "a key")]
    public async Task Uncommitted_writes_overlap_a_read_as_an_invalidation_would(
        string reads,
        string writes
    )
    {
        await using var node = new SluiceNode();
        var read = reads == "a key" ? _rows.For(1) : _rows.All;
        var written = writes == "a key" ? _rows.For(5) : _rows.All;

        using var flow = SluiceFlow.Begin();
        FlowWrites.AddProbe(node.Cache, Pending(written));
        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _outer,
                    "k",
                    (r, _) =>
                    {
                        r.From(read);
                        return Task.FromResult("value");
                    }
                )
                .AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                Uncommitted.Replace("*", writes == "a key" ? "guard-rows:5" : "guard-rows (all)")
            );
    }

    [Fact]
    public async Task Another_cache_s_uncommitted_writes_are_ignored()
    {
        await using var node = new SluiceNode();
        await using var other = new SluiceNode();
        var asked = false;

        using var flow = SluiceFlow.Begin();
        FlowWrites.AddProbe(
            other.Cache,
            () =>
            {
                asked = true;
                return [_rows.For(1)];
            }
        );

        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#1");
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task A_read_inside_an_ambient_transaction_with_uncommitted_writes_it_depends_on_throws()
    {
        await using var node = new SluiceNode();
        var asked = new List<Transaction>();
        FlowWrites.OnAmbient(
            node.Cache,
            transaction =>
            {
                asked.Add(transaction);
                return [_rows.For(1)];
            }
        );

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var caller = Transaction.Current;
            var call = () => node.Cache.GetOrSetAsync(_outer, "k", Good).AsTask();

            await call.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(Uncommitted.Replace("*", "guard-rows:1"));
            asked.Should().ContainSingle().Which.Should().BeSameAs(caller);
            Transaction.Current.Should().BeSameAs(caller);
        }

        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
        (await node.Cache.GetOrSetAsync(_outer, "k", Good)).Should().Be("good#2");
        asked.Should().ContainSingle("without an ambient transaction there's nothing to ask about");
    }

    [Fact]
    public async Task A_nested_flow_shares_the_outer_box_and_only_the_outermost_handle_removes_it()
    {
        await using var node = new SluiceNode();
        var call = () => node.Cache.GetOrSetAsync(_outer, "k", Good).AsTask();

        var outer = SluiceFlow.Begin();
        using (SluiceFlow.Begin())
        {
            FlowWrites.AddProbe(node.Cache, Pending(_rows.For(1))).Should().BeTrue();
        }

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(Uncommitted);
        FlowWrites.AddProbe(node.Cache, Pending()).Should().BeTrue();

        outer.Dispose();

        FlowWrites.AddProbe(node.Cache, Pending()).Should().BeFalse();
        (await call()).Should().Be("good#2");
        (await call()).Should().Be("good#2");
    }
}
