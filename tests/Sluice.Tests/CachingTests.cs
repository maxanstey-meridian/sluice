using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

public sealed class CachingTests
{
    private static readonly Source<int> _rows = new("caching-rows");
    private static readonly Query<string, string> _summary = new("caching-summary");
    private static readonly Query<string, string> _report = new("caching-report");

    [Fact]
    public async Task A_second_call_for_the_same_key_does_not_run_the_compute()
    {
        await using var node = new SluiceNode();
        var reader = new CountingReader(node.Cache, _summary);

        var first = await reader.GetAsync("a", _rows.For(1));
        var second = await reader.GetAsync("a", _rows.For(1));

        second.Should().Be(first);
        reader.Runs("a").Should().Be(1);
    }

    [Fact]
    public async Task A_query_duration_overrides_the_cache_default_duration()
    {
        // A zero default duration means nothing is kept unless the query says otherwise.
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o => o.Duration = TimeSpan.Zero)
        );
        var kept = new CountingReader(
            node.Cache,
            new Query<string, string>("caching-kept") { Duration = TimeSpan.FromMinutes(5) }
        );
        var defaulted = new CountingReader(node.Cache, _report);

        await kept.GetAsync("a", _rows.For(1));
        await kept.GetAsync("a", _rows.For(1));
        await defaulted.GetAsync("a", _rows.For(1));
        await defaulted.GetAsync("a", _rows.For(1));

        kept.Runs("a").Should().Be(1);
        defaulted.Runs("a").Should().Be(2);
    }

    [Fact]
    public async Task Two_queries_with_the_same_key_do_not_share_entries()
    {
        await using var node = new SluiceNode();
        var summary = new CountingReader(node.Cache, _summary);
        var report = new CountingReader(node.Cache, _report);

        await summary.GetAsync("a", _rows.For(1));
        await report.GetAsync("a", _rows.For(1));

        summary.Runs("a").Should().Be(1);
        report.Runs("a").Should().Be(1);
    }

    [Fact]
    public async Task A_keyless_query_caches_one_value_until_what_it_read_changes()
    {
        await using var node = new SluiceNode();
        var everything = new Query<string>("caching-everything");
        var runs = 0;

        Task<string> Get() =>
            node
                .Cache.GetOrSetAsync(
                    everything,
                    (read, _) =>
                    {
                        read.From(_rows.All);
                        return Task.FromResult($"all#{++runs}");
                    }
                )
                .AsTask();

        (await Get()).Should().Be("all#1");
        (await Get()).Should().Be("all#1");
        await node.Cache.InvalidateAsync(_rows.For(7));
        (await Get()).Should().Be("all#2");
    }

    [Fact]
    public async Task An_uncached_value_goes_to_the_caller_and_the_next_call_computes_again()
    {
        await using var node = new SluiceNode();
        var query = new Query<string, string>("caching-uncached");
        var runs = 0;

        Task<string> Get() =>
            node
                .Cache.GetOrSetAsync(
                    query,
                    "missing",
                    (read, _) =>
                    {
                        read.DoNotCache();
                        return Task.FromResult($"miss#{++runs}");
                    }
                )
                .AsTask();

        (await Get()).Should().Be("miss#1", "a compute that doesn't cache needs no dependency");
        (await Get()).Should().Be("miss#2");
    }

    [Fact]
    public async Task A_compute_can_leave_its_value_uncached_from_inside_a_fused_fetch()
    {
        await using var node = new SluiceNode();
        var query = new Query<string, string>("caching-uncached-fetch");
        var runs = 0;

        Task<string> Get() =>
            node
                .Cache.GetOrSetAsync(
                    query,
                    "k",
                    (read, _) =>
                        read.From(
                            _rows.For(1),
                            _ =>
                            {
                                read.DoNotCache();
                                return Task.FromResult($"fetched#{++runs}");
                            }
                        )
                )
                .AsTask();

        (await Get()).Should().Be("fetched#1");
        (await Get()).Should().Be("fetched#2");
    }

    // FusionCache hands a waiting caller only what the running compute stored, so with nothing stored the waiter runs
    // its own compute once the first ends.
    [Fact]
    public async Task A_caller_waiting_on_an_uncached_compute_gets_a_value_from_its_own_compute()
    {
        await using var node = new SluiceNode();
        var query = new Query<string, string>("caching-uncached-waiters");
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runs = 0;
        var firstEnded = false;

        async Task<string> Compute(SluiceReads read, CancellationToken ct)
        {
            var run = Interlocked.Increment(ref runs);
            if (run == 1)
            {
                started.SetResult();
                await release.Task;
            }
            else
            {
                firstEnded
                    .Should()
                    .BeTrue("a waiter's compute runs once the first has ended, not beside it");
            }

            read.DoNotCache();
            firstEnded = run == 1 || firstEnded;
            return $"run#{run}";
        }

        var first = node.Cache.GetOrSetAsync(query, "k", Compute).AsTask();
        await started.Task;
        var second = node.Cache.GetOrSetAsync(query, "k", Compute).AsTask();
        release.SetResult();

        (await first).Should().Be("run#1");
        (await second).Should().Be("run#2");
    }

    [Fact]
    public async Task A_violation_still_throws_and_caches_nothing_when_the_compute_left_its_value_uncached()
    {
        await using var node = new SluiceNode();
        var query = new Query<string, string>("caching-uncached-violation");
        var runs = 0;

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    query,
                    "k",
                    async (read, _) =>
                    {
                        runs++;
                        try
                        {
                            await node.Cache.GetOrSetAsync(
                                _summary,
                                "inner",
                                (_, _) => Task.FromResult("")
                            );
                        }
                        catch (InvalidOperationException)
                        {
                            // Swallowed: the violation is still the call's.
                        }

                        read.DoNotCache();
                        return "value";
                    }
                )
                .AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*called from inside another compute*");
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached, so the second call computed again");
    }

    [Fact]
    public async Task Leaving_a_value_uncached_after_the_compute_returned_throws()
    {
        await using var node = new SluiceNode();
        var query = new Query<string, string>("caching-uncached-late");
        SluiceReads? kept = null;

        await node.Cache.GetOrSetAsync(
            query,
            "k",
            (read, _) =>
            {
                kept = read;
                read.From(_rows.For(1));
                return Task.FromResult("value");
            }
        );

        var late = () => kept!.DoNotCache();

        late.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*after the compute returned*");
    }
}
