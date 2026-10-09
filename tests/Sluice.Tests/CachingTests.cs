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
}
