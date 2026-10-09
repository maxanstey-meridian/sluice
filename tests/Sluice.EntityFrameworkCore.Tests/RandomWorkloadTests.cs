using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// Random writes over related types (cascades, set-null keys, a self-referencing cascade, table-per-type inheritance,
/// composite keys and a many-to-many join), checked against the one thing that matters: every cached read equals the
/// same read run fresh. Set <c>SLUICE_RANDOM_SEEDS</c> to run more seeds than the default.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RandomWorkloadTests(PostgresFixture postgres)
{
    private const int Steps = 60;
    private const int ConcurrentWriters = 4;
    private const int ConcurrentReaders = 4;
    private const int ConcurrentSteps = 40;

    private static readonly Query<string, string> _view = new("ef-random-view");

    public static TheoryData<int> Seeds() =>
        new(
            Enumerable.Range(
                1,
                int.TryParse(
                    Environment.GetEnvironmentVariable("SLUICE_RANDOM_SEEDS"),
                    out var seeds
                )
                    ? seeds
                    : 3
            )
        );

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task After_each_random_write_every_cached_read_matches_a_fresh_one(int seed)
    {
        await using var node = new SluiceNode();
        await using var oracle = new SluiceNode();
        await using var writer = postgres.CreateDb(node);
        await using var reader = postgres.CreateDb(node);
        var first = await Keys.Of(writer);
        var last = first;
        var workload = new RandomWriter(writer, node.Cache, first, new Random(seed));
        var log = new List<string>();

        for (var step = 1; step <= Steps; step++)
        {
            log.Add($"{step}: {await workload.Step()}");
            last = last.Max(await Keys.Of(writer));

            var stale = await Stale(node, oracle, reader, ShopViews.Of(first, last), step);
            stale
                .Should()
                .BeEmpty($"seed {seed} served stale reads after:\n{string.Join("\n", log)}");
        }

        log.Count(line => line.Contains("refused", StringComparison.Ordinal))
            .Should()
            .BeLessThan(
                Steps / 4,
                "most steps should change something:\n{0}",
                string.Join("\n", log)
            );
    }

    // Each compute starts before some invalidations and after others, so this checks the timing a sequential run can't:
    // a read cached across a concurrent commit must be dead by the time it's read again.
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Once_concurrent_random_reads_and_writes_settle_every_cached_read_matches_a_fresh_one(
        int seed
    )
    {
        await using var node = new SluiceNode();
        await using var oracle = new SluiceNode();
        await using var checker = postgres.CreateDb(node);
        var first = await Keys.Of(checker);
        using var writing = new CancellationTokenSource();

        var writers = Enumerable
            .Range(0, ConcurrentWriters)
            .Select(async i =>
            {
                await using var db = postgres.CreateDb(node);
                var workload = new RandomWriter(db, node.Cache, first, new Random(seed * 100 + i));
                var log = new List<string>();
                for (var step = 0; step < ConcurrentSteps; step++)
                {
                    log.Add(await workload.Step());
                }

                return log;
            })
            .ToList();
        // Each reader returns the highest keys it saw, so the check covers every view it cached, deleted rows included.
        var readers = Enumerable
            .Range(0, ConcurrentReaders)
            .Select(async i =>
            {
                await using var db = postgres.CreateDb(node);
                var random = new Random(seed * 100 + 50 + i);
                var seen = first;
                while (!writing.IsCancellationRequested)
                {
                    seen = seen.Max(await Keys.Of(db));
                    var views = ShopViews.Of(first, seen);
                    for (var read = 0; read < 20; read++)
                    {
                        var view = views[random.Next(views.Count)];
                        await node.Cache.GetOrSetAsync(
                            _view,
                            view.Name,
                            (reads, ct) => view.Read(reads, db, ct)
                        );
                    }
                }

                return seen;
            })
            .ToList();

        List<string> log;
        try
        {
            log = [.. (await Task.WhenAll(writers)).SelectMany(steps => steps)];
        }
        finally
        {
            await writing.CancelAsync();
        }

        var last = (await Task.WhenAll(readers)).Aggregate(
            await Keys.Of(checker),
            (keys, seen) => keys.Max(seen)
        );
        var stale = await Stale(node, oracle, checker, ShopViews.Of(first, last), 0);
        stale.Should().BeEmpty($"seed {seed} served stale reads once its writes had settled");
        log.Count(line => line.StartsWith("refused", StringComparison.Ordinal))
            .Should()
            .BeLessThan(
                log.Count / 4,
                "most steps should change something:\n{0}",
                string.Join("\n", log)
            );
    }

    // Reads each view through the node's cache, caching it if it isn't, and fresh, through a cache that has never
    // seen the key. A mismatch is a cached value no write evicted though its data changed.
    private static async Task<List<string>> Stale(
        SluiceNode node,
        SluiceNode oracle,
        ShopDb reader,
        IReadOnlyList<ShopView> views,
        int check
    )
    {
        var stale = new List<string>();
        foreach (var view in views)
        {
            var cached = await node.Cache.GetOrSetAsync(
                _view,
                view.Name,
                (read, ct) => view.Read(read, reader, ct)
            );
            var fresh = await oracle.Cache.GetOrSetAsync(
                _view,
                $"{view.Name} #{check}",
                (read, ct) => view.Read(read, reader, ct)
            );
            if (cached != fresh)
            {
                stale.Add($"{view.Name}: cached {cached}, actually {fresh}");
            }
        }

        return stale;
    }
}
