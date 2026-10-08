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

    // T9
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

    // T9
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
}
