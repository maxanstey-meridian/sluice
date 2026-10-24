using Sluice.Infrastructure;
using Sluice.Tests.Support;

namespace Sluice.Tests;

public sealed class RaceTests
{
    private static readonly Source<int> _rows = new("race-rows");
    private static readonly Query<string, string> _view = new("race-view");

    private readonly Dictionary<int, string> _data = new() { [1] = "old", [2] = "other" };
    private readonly TaskCompletionSource _reading = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _gate = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _runs;

    // The first run reads row 1, then waits at the gate until the test has written and invalidated.
    private Task<string> View(SluiceCache cache) =>
        cache
            .GetOrSetAsync(
                _view,
                "k",
                async (read, _) =>
                {
                    var run = ++_runs;
                    read.From(_rows.For(1));
                    var value = _data[1];
                    if (run == 1)
                    {
                        _reading.SetResult();
                        await _gate.Task;
                    }

                    return value;
                }
            )
            .AsTask();

    [Fact]
    public async Task An_overlapping_invalidation_mid_compute_returns_the_value_but_leaves_no_entry()
    {
        await using var node = new SluiceNode();
        var racing = View(node.Cache);
        await _reading.Task;

        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));
        _gate.SetResult();

        (await racing).Should().Be("old");
        (await View(node.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task A_non_overlapping_invalidation_mid_compute_keeps_the_entry()
    {
        await using var node = new SluiceNode();
        var racing = View(node.Cache);
        await _reading.Task;

        _data[2] = "changed";
        await node.Cache.InvalidateAsync(_rows.For(2));
        _gate.SetResult();

        (await racing).Should().Be("old");
        (await View(node.Cache)).Should().Be("old");
        _runs.Should().Be(1);
    }

    // A caller with uncommitted writes stays out of FusionCache, stampede lock included, so a caller without them,
    // computing the same key at the same time, never waits on it or gets its violation, whichever starts first.
    // Each call that mustn't wait has a timeout, so a regression fails rather than hangs.
    [Theory]
    [InlineData("the caller with uncommitted writes")]
    [InlineData("the caller without")]
    public async Task Callers_with_and_without_uncommitted_writes_on_one_key_neither_wait_on_nor_fail_each_other(
        string first
    )
    {
        await using var node = new SluiceNode();
        var mayFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var quickRuns = 0;

        // Holds its compute open until the test lets it finish.
        async Task<string> Held(SluiceReads read, CancellationToken ct)
        {
            read.From(_rows.For(1));
            _reading.SetResult();
            await mayFinish.Task;
            return "held";
        }

        Task<string> Quick(SluiceReads read, CancellationToken ct)
        {
            read.From(_rows.For(1));
            return Task.FromResult($"quick#{++quickRuns}");
        }

        // Its box is set inside this method, so the test's own flow never has one.
        async Task<string> WithWrites(Func<SluiceReads, CancellationToken, Task<string>> compute)
        {
            using var flow = SluiceFlow.Begin();
            FlowWrites.AddProbe(node.Cache, () => [_rows.For(1)]);
            return await node.Cache.GetOrSetAsync(_view, "k", compute);
        }

        Task<string> Without(Func<SluiceReads, CancellationToken, Task<string>> compute) =>
            node.Cache.GetOrSetAsync(_view, "k", compute).AsTask();

        var timeout = TimeSpan.FromSeconds(10);
        if (first == "the caller with uncommitted writes")
        {
            var withWrites = WithWrites(Held);
            await _reading.Task;

            (await Without(Quick).WaitAsync(timeout)).Should().Be("quick#1");
            mayFinish.SetResult();

            await withWrites
                .Awaiting(call => call)
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Query 'race-view' depends on race-rows:1, *");
            (await Without(Quick)).Should().Be("quick#1");
        }
        else
        {
            var without = Without(Held);
            await _reading.Task;

            await WithWrites(Quick)
                .WaitAsync(timeout)
                .Awaiting(call => call)
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Query 'race-view' depends on race-rows:1, *");
            mayFinish.SetResult();

            (await without).Should().Be("held");
            (await Without(Quick)).Should().Be("held");
        }
    }
}
