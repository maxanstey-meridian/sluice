using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Sluice.Infrastructure;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

// SLUICE-CORE-024. FusionCache stamps and expires entries on DateTimeOffset.UtcNow, not on a TimeProvider, so the tests
// that watch a stale copy expire run on real time, with a MaxStaleness of a second and every assertion well inside or
// well outside it. The rest use a MaxStaleness no test outlives.
public sealed class StalenessTests
{
    private static readonly TimeSpan _n = TimeSpan.FromSeconds(1);
    private static readonly Source<int> _rows = new("stale-rows");
    private static readonly Query<string, string> _view = new("stale-view") { MaxStaleness = _n };
    private static readonly Query<string, string> _patient = new("stale-patient")
    {
        MaxStaleness = TimeSpan.FromMinutes(1),
    };
    private static readonly Query<string, string> _strict = new("stale-strict");

    private readonly MemoryDistributedCache _l2 = new(
        Options.Create(new MemoryDistributedCacheOptions())
    );
    private readonly string _bus = Guid.NewGuid().ToString();
    private readonly Dictionary<int, string> _data = new() { [1] = "old", [2] = "old" };
    private int _runs;

    // When the latest compute began, on the monotonic clock: a stale copy expires no later than N after it.
    private long _computeBegan;

    // Reads row's value through the query, keyed by the row; `slow` delays the first compute after it read.
    private Task<string> Read(
        SluiceCache cache,
        Query<string, string>? query = null,
        int row = 1,
        TimeSpan slow = default
    ) =>
        cache
            .GetOrSetAsync(
                query ?? _view,
                $"{row}",
                async (read, _) =>
                {
                    _computeBegan = Stopwatch.GetTimestamp();
                    var run = Interlocked.Increment(ref _runs);
                    read.From(_rows.For(row));
                    var value = _data[row];
                    if (run == 1 && slow > TimeSpan.Zero)
                    {
                        await Task.Delay(slow);
                    }

                    return value;
                }
            )
            .AsTask();

    private static async Task Until(long began, TimeSpan after)
    {
        var left = after - Stopwatch.GetElapsedTime(began);
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left);
        }
    }

    private static async Task<bool> HasStaleCopy(SluiceNode node, Query<string, string> query) =>
        (await node.Fusion.TryGetAsync<string>($":{query.EntryKey("1")}")).HasValue;

    [Fact]
    public async Task An_invalidated_value_is_served_within_MaxStaleness_and_recomputed_after_it()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache);
        var began = _computeBegan;
        (await node.Fusion.TryGetAsync<string>(":stale-view:1"))
            .GetValueOrDefault()
            .Should()
            .Be("old", "the stale copy's key is ':' plus the entry key");

        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));

        (await Read(node.Cache))
            .Should()
            .Be("old", "it's within MaxStaleness of the compute's start");
        await Until(began, _n * 1.5);
        (await Read(node.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task Without_writes_a_staleness_query_keeps_its_entry_past_MaxStaleness()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache);

        await Until(_computeBegan, _n * 1.5);
        await Read(node.Cache);

        _runs.Should().Be(1, "the entry lives its Duration, the cache's default of 30 s");
    }

    [Fact]
    public async Task Invalidations_within_MaxStaleness_cost_one_compute()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache);
        var began = _computeBegan;

        for (var write = 1; write <= 5; write++)
        {
            _data[1] = $"write {write}";
            await node.Cache.InvalidateAsync(_rows.For(1));
            (await Read(node.Cache)).Should().Be("old");
        }

        await Until(began, _n * 1.5);
        (await Read(node.Cache)).Should().Be("write 5");
        (await Read(node.Cache)).Should().Be("write 5");
        _runs.Should().Be(2);
    }

    // A copy written anyway would be dead on arrival, so no read could tell; FusionCache would still remove it from
    // memory, write it to the distributed cache or remove it there, and tell the other nodes. So this watches the
    // writes. Handlers run inline, so each has reported by the time the read returns.
    [Fact]
    public async Task A_compute_that_outlasts_MaxStaleness_leaves_no_stale_copy()
    {
        await using var node = SluiceNode.Clustered(
            _l2,
            _bus,
            cache => cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true)
        );
        var copyKey = $"{SluiceCache.CacheName}::{_view.EntryKey("1")}";
        var writes = new ConcurrentQueue<string>();
        node.Fusion.Events.Memory.Set += (_, e) => Record(e.Key, "memory set");
        node.Fusion.Events.Memory.Remove += (_, e) => Record(e.Key, "memory remove");
        node.Fusion.Events.Distributed.Set += (_, e) => Record(e.Key, "distributed set");
        node.Fusion.Events.Distributed.Remove += (_, e) => Record(e.Key, "distributed remove");
        node.Fusion.Events.Backplane.MessagePublished += (_, e) =>
            Record(e.Message.CacheKey, "backplane");

        void Record(string? key, string write)
        {
            if (key == copyKey)
            {
                writes.Enqueue(write);
            }
        }

        await Read(node.Cache, slow: _n * 1.2);
        writes.Should().BeEmpty();

        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));

        (await Read(node.Cache)).Should().Be("new");
        _runs.Should().Be(2);
        writes
            .Should()
            .Contain(
                ["memory set", "distributed set", "backplane"],
                "a quick compute writes its copy, which these handlers see"
            );
    }

    // Node A's compute takes 0.6 N, so its copy reaches the distributed cache 0.6 N after its stamp. Node B reads it
    // from there and keeps it until N after that stamp; given what was left of N, as memory is, it would have expired
    // 0.4 N after the stamp, before it was written. B reads as soon as A's invalidation returns, for the most margin.
    [Fact]
    public async Task Another_node_keeps_a_slow_compute_s_stale_copy_for_MaxStaleness_from_its_start()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await Read(a.Cache, slow: _n * 0.6);
        var began = _computeBegan;
        _data[1] = "new";
        await a.Cache.InvalidateAsync(_rows.For(1));

        (await Read(b.Cache)).Should().Be("old", "node B reads node A's copy, which lives N");
        _runs.Should().Be(1);

        await Until(began, _n * 1.25);

        (await Read(b.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    // Another caller stores the normal entry between this caller's reads and its copy's lock: the copy's factory finds
    // it there, and can't know its tags, so it writes no copy. One written anyway would carry no now-tags, and outlive
    // the normal entry's invalidation. Handlers run inline, so the entry is there before the copy's factory runs.
    [Fact]
    public async Task An_inner_hit_leaves_no_stale_copy()
    {
        await using var node = new SluiceNode(cache =>
            cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true)
        );
        var copyKey = $"{SluiceCache.CacheName}::{_view.EntryKey("1")}";
        var arranged = 0;
        node.Fusion.Events.Miss += (_, e) =>
        {
            // The copy's read misses, just before its GetOrSetAsync takes the lock.
            if (e.Key == copyKey && Interlocked.Exchange(ref arranged, 1) == 0)
            {
                node.Fusion.Set(
                    _view.EntryKey("1"),
                    "theirs",
                    tags: ["stale-rows:1", "stale-rows:"]
                );
            }
        };

        (await Read(node.Cache)).Should().Be("theirs");
        _runs.Should().Be(0);
        (await HasStaleCopy(node, _view)).Should().BeFalse();

        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));

        (await Read(node.Cache)).Should().Be("new");
        _runs.Should().Be(1);
    }

    // Counted from the end, the copy would last until 1.5 N from the compute's start, so the read at 1.25 N would get it.
    [Fact]
    public async Task The_stale_copy_expires_MaxStaleness_after_its_compute_began_not_after_it_ended()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, slow: _n * 0.5);
        var began = _computeBegan;
        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));
        (await Read(node.Cache)).Should().Be("old");

        await Until(began, _n * 1.25);

        (await Read(node.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    // The app's fail-safe keeps the normal entry in memory long past its Duration, and asks every read to return an
    // expired entry; node B keeps the copy it read from the distributed cache in memory for N from that read, which is
    // 0.5 N after the copy's compute began, so it's still there, expired, at 1.1 N. Both entries' reads refuse what's
    // expired whatever the app asks, so neither is served once its life has ended.
    [Fact]
    public async Task With_fail_safe_on_neither_entry_is_served_past_its_life()
    {
        static void FailSafe(IFusionCacheBuilder cache) =>
            cache.WithDefaultEntryOptions(o =>
            {
                o.IsFailSafeEnabled = true;
                o.FailSafeMaxDuration = TimeSpan.FromHours(1);
                o.AllowStaleOnReadOnly = true;
            });
        var shortLived = new Query<string, string>("stale-short-lived")
        {
            Duration = _n,
            MaxStaleness = _n,
        };
        await using var a = SluiceNode.Clustered(_l2, _bus, FailSafe);
        await using var b = SluiceNode.Clustered(_l2, _bus, FailSafe);
        await Read(a.Cache, shortLived, row: 1);
        await Read(a.Cache, shortLived, row: 2);
        var lastBegan = _computeBegan;

        // Row 1 is quiet: nothing invalidates it, so only its Duration ends it.
        _data[1] = "new";
        _data[2] = "new";
        await a.Cache.InvalidateAsync(_rows.For(2));
        await Until(lastBegan, _n * 0.5);
        (await Read(b.Cache, shortLived, row: 2))
            .Should()
            .Be(
                "old",
                "node B reads the copy from the distributed cache, and keeps it for N from now"
            );

        await Until(lastBegan, _n * 1.1);

        // Both expired entries are still there for a read that asks for one, so the reads below refuse them rather
        // than find nothing.
        static FusionCacheEntryOptions Permissive(SluiceNode node)
        {
            var options = node.Fusion.DefaultEntryOptions.Duplicate();
            options.AllowStaleOnReadOnly = true;
            return options;
        }
        (await a.Fusion.TryGetAsync<string>(shortLived.EntryKey("1"), Permissive(a)))
            .GetValueOrDefault()
            .Should()
            .Be("old", "fail-safe keeps node A's expired entry");
        (await b.Fusion.TryGetAsync<string>($":{shortLived.EntryKey("2")}", Permissive(b)))
            .GetValueOrDefault()
            .Should()
            .Be("old", "node B keeps the copy for N from its read");

        (await Read(a.Cache, shortLived, row: 1))
            .Should()
            .Be("new", "the normal entry's Duration has passed");
        (await Read(b.Cache, shortLived, row: 2)).Should().Be("new", "the copy's N has passed");
        _runs.Should().Be(4);
    }

    [Fact]
    public async Task Another_node_serves_the_stale_copy_from_L2_until_MaxStaleness_and_then_recomputes()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await Read(a.Cache);
        var began = _computeBegan;
        _data[1] = "new";
        await a.Cache.InvalidateAsync(_rows.For(1));

        (await Read(b.Cache)).Should().Be("old");
        _runs.Should().Be(1, "node B read node A's stale copy from the distributed cache");

        await Until(began, _n * 1.5);

        (await Read(b.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    // Both nodes hold the copy in memory before the hard eviction. Node A's clock moves only when the test advances
    // it, so its re-publish, the one invalidation it makes, lands when the test says. Every hard publisher is a row:
    // InvalidateNow*, InvalidateAsync inside SluiceFlow.InvalidateNow, and a package's publish of an AsNow dependency.
    [Theory]
    [InlineData("InvalidateNowAsync(dependency)")]
    [InlineData("InvalidateNowAsync(dependencies)")]
    [InlineData("InvalidateNow")]
    [InlineData("InvalidateAsync inside SluiceFlow.InvalidateNow")]
    [InlineData("FlowWrites.PublishAsync of an AsNow dependency")]
    [InlineData("FlowWrites.Publish of an AsNow dependency")]
    public async Task InvalidateNowAsync_evicts_stale_copies_at_once_on_every_node(string call)
    {
        var time = new FakeTimeProvider();
        await using var a = SluiceNode.Clustered(
            _l2,
            _bus,
            services: services => services.AddSingleton<TimeProvider>(time)
        );
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await Read(a.Cache, _patient);
        _data[1] = "new";
        await b.Cache.InvalidateAsync(_rows.For(1));
        (await Read(a.Cache, _patient)).Should().Be("old");
        (await Read(b.Cache, _patient)).Should().Be("old");

        switch (call)
        {
            case "InvalidateNowAsync(dependency)":
                await a.Cache.InvalidateNowAsync(_rows.For(1));
                break;
            case "InvalidateNowAsync(dependencies)":
                await a.Cache.InvalidateNowAsync([_rows.For(1)]);
                break;
            case "InvalidateNow":
                a.Cache.InvalidateNow(_rows.For(1));
                break;
            case "InvalidateAsync inside SluiceFlow.InvalidateNow":
                using (SluiceFlow.InvalidateNow())
                {
                    await a.Cache.InvalidateAsync(_rows.For(1));
                }

                break;
            case "FlowWrites.PublishAsync of an AsNow dependency":
                await FlowWrites.PublishAsync(a.Cache, [_rows.For(1).AsNow()]);
                break;
            default:
                FlowWrites.Publish(a.Cache, [_rows.For(1).AsNow()]);
                break;
        }

        (await HasStaleCopy(a, _patient)).Should().BeFalse();
        (await HasStaleCopy(b, _patient)).Should().BeFalse();
        (await Read(b.Cache, _patient)).Should().Be("new");
        (await Read(a.Cache, _patient)).Should().Be("new");

        var published = new ConcurrentQueue<string>();
        var republished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        a.Fusion.Events.RemoveByTag += (_, e) =>
        {
            published.Enqueue(e.Tag);
            if (published.Count == 4)
            {
                republished.TrySetResult();
            }
        };
        time.Advance(SluiceCache.RepublishDelay);
        await republished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        published
            .Should()
            .BeEquivalentTo(["stale-rows:1", "stale-rows:*", "!stale-rows:1", "!stale-rows:*"]);
    }

    // The copy is stamped before its compute begins, so a hard eviction landing mid-compute removes it with the entry.
    [Fact]
    public async Task A_value_computed_across_an_InvalidateNow_is_not_served_after_it()
    {
        await using var node = new SluiceNode();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var computing = node
            .Cache.GetOrSetAsync(
                _patient,
                "1",
                async (read, _) =>
                {
                    read.From(_rows.For(1));
                    var value = _data[1];
                    began.SetResult();
                    await release.Task;
                    return value;
                }
            )
            .AsTask();
        await began.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _data[1] = "new";
        await node.Cache.InvalidateNowAsync(_rows.For(1));
        release.SetResult();

        (await computing).Should().Be("old", "the caller gets what its own compute read");
        (await Read(node.Cache, _patient)).Should().Be("new");
    }

    // With a distributed cache or a backplane, FusionCache can't just empty memory: each node drops an entry stamped
    // before the clear when it next reads it, so that check is what removes each node's copy. Both nodes hold one.
    [Fact]
    public async Task ClearAsync_evicts_stale_copies_on_every_node()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await Read(a.Cache, _patient);
        _data[1] = "new";
        await a.Cache.InvalidateAsync(_rows.For(1));
        (await Read(a.Cache, _patient)).Should().Be("old");
        (await Read(b.Cache, _patient)).Should().Be("old");

        await a.Cache.ClearAsync();

        (await HasStaleCopy(a, _patient)).Should().BeFalse();
        (await HasStaleCopy(b, _patient)).Should().BeFalse();
        (await Read(b.Cache, _patient)).Should().Be("new");
        (await Read(a.Cache, _patient)).Should().Be("new", "node A reads node B's new entry");
        _runs.Should().Be(2);
    }

    // No SluiceFlow.Begin: the scope stands alone.
    [Theory]
    [InlineData("InvalidateAsync(dependency)")]
    [InlineData("InvalidateAsync(dependencies)")]
    [InlineData("Invalidate")]
    public async Task Inside_SluiceFlow_InvalidateNow_InvalidateAsync_and_Invalidate_evict_stale_copies(
        string call
    )
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, _patient);
        _data[1] = "new";

        using (SluiceFlow.InvalidateNow())
        {
            switch (call)
            {
                case "InvalidateAsync(dependency)":
                    await node.Cache.InvalidateAsync(_rows.For(1));
                    break;
                case "InvalidateAsync(dependencies)":
                    await node.Cache.InvalidateAsync([_rows.For(1)]);
                    break;
                default:
                    node.Cache.Invalidate(_rows.For(1));
                    break;
            }
        }

        (await HasStaleCopy(node, _patient)).Should().BeFalse();
        (await Read(node.Cache, _patient)).Should().Be("new");
    }

    // A package publishes what it captured as each write was marked when it started (SLUICE-EF-028), so a write made
    // outside the scope stays soft when its transaction commits, and publishes, inside one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_package_s_publish_of_a_plain_dependency_inside_SluiceFlow_InvalidateNow_leaves_the_stale_copy(
        bool asynchronously
    )
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, _patient);
        _data[1] = "new";

        using (SluiceFlow.InvalidateNow())
        {
            if (asynchronously)
            {
                await FlowWrites.PublishAsync(node.Cache, [_rows.For(1)]);
            }
            else
            {
                FlowWrites.Publish(node.Cache, [_rows.For(1)]);
            }
        }

        (await HasStaleCopy(node, _patient)).Should().BeTrue();
        (await Read(node.Cache, _patient)).Should().Be("old");
    }

    // Disposing a scope puts back what the flow had, so the inner one leaves the outer on, and the outer turns it off.
    [Fact]
    public async Task SluiceFlow_InvalidateNow_nests_and_disposing_it_puts_back_what_the_flow_had()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, _patient, row: 1);
        await Read(node.Cache, _patient, row: 2);
        _data[1] = _data[2] = "new";

        using (SluiceFlow.InvalidateNow())
        {
            using (SluiceFlow.InvalidateNow())
            {
                FlowWrites.Now.Should().BeTrue();
            }

            await node.Cache.InvalidateAsync(_rows.For(1));
        }

        FlowWrites.Now.Should().BeFalse();
        await node.Cache.InvalidateAsync(_rows.For(2));

        (await Read(node.Cache, _patient, row: 1))
            .Should()
            .Be("new", "the outer scope was still open after the inner one was disposed");
        (await Read(node.Cache, _patient, row: 2))
            .Should()
            .Be("old", "outside every scope, InvalidateAsync leaves the stale copy");
    }

    // A task captures the flow's async state when it starts, so it keeps the flag whatever the flow does after.
    [Fact]
    public async Task A_task_started_inside_SluiceFlow_InvalidateNow_keeps_it_after_the_scope_is_disposed()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, _patient);
        _data[1] = "new";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task invalidating;

        using (SluiceFlow.InvalidateNow())
        {
            invalidating = Task.Run(async () =>
            {
                await release.Task;
                await node.Cache.InvalidateAsync(_rows.For(1));
            });
        }

        FlowWrites.Now.Should().BeFalse();
        release.SetResult();
        await invalidating.WaitAsync(TimeSpan.FromSeconds(10));

        (await HasStaleCopy(node, _patient)).Should().BeFalse();
        (await Read(node.Cache, _patient)).Should().Be("new");
    }

    // Source.All is one instance every reader and writer of the source shares, so marking it in place would make every
    // later invalidation of the source hard.
    [Fact]
    public async Task AsNow_never_changes_the_dependency_it_is_called_on()
    {
        await using var node = new SluiceNode();
        var all = _rows.All;

        var hard = all.AsNow();

        hard.Should().NotBeSameAs(all);
        hard.AsNow().Should().BeSameAs(hard);
        hard.InvalidationTags.Should().Equal(all.InvalidationTags);
        hard.PublishedTags.Should().Equal("stale-rows:", "!stale-rows:");
        _rows.All.PublishedTags.Should().Equal("stale-rows:");

        await Read(node.Cache, _patient);
        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.All);
        (await Read(node.Cache, _patient)).Should().Be("old");
    }

    // A copy written anyway would hold the violation's default value, or the uncached one, and the next read would
    // return it instead of computing.
    [Theory]
    [InlineData("violation")]
    [InlineData("DoNotCache")]
    public async Task A_violation_or_DoNotCache_leaves_no_stale_copy_and_still_reaches_the_caller(
        string outcome
    )
    {
        await using var node = new SluiceNode();
        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _patient,
                    "1",
                    (read, _) =>
                    {
                        if (outcome == "DoNotCache")
                        {
                            read.DoNotCache();
                        }

                        return Task.FromResult("first");
                    }
                )
                .AsTask();

        if (outcome == "violation")
        {
            await call.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*recorded no dependencies*");
        }
        else
        {
            (await call()).Should().Be("first");
        }

        (await HasStaleCopy(node, _patient)).Should().BeFalse();
        (await Read(node.Cache, _patient)).Should().Be("old");
        _runs.Should().Be(1);
    }

    // Only the stale copy holds a value when the flow reads, and the flow's read neither returns it nor stores its own.
    [Fact]
    public async Task With_uncommitted_writes_a_staleness_query_takes_the_bypass_and_stores_nothing()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, _patient);
        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));

        using (SluiceFlow.Begin())
        {
            FlowWrites.AddProbe(node.Cache, "unrelated", () => [_rows.For(2)]);

            (await Read(node.Cache, _patient)).Should().Be("new");
            (await Read(node.Cache, _patient)).Should().Be("new");
            _runs.Should().Be(3, "each read in the flow computes");
        }

        using (SluiceFlow.Begin())
        {
            FlowWrites.AddProbe(node.Cache, "overlapping", () => [_rows.For(1)]);

            var read = () => Read(node.Cache, _patient);

            await read.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(
                    "*depends on stale-rows:1, which the caller's own transaction has written*"
                );
        }

        (await Read(node.Cache, _patient)).Should().Be("old", "the flows stored nothing");
        _runs.Should().Be(4);
    }

    [Fact]
    public async Task A_stale_copy_carries_the_query_s_size_under_a_size_limit()
    {
        using var memory = new MemoryCache(
            new MemoryCacheOptions { SizeLimit = 10, TrackStatistics = true }
        );
        await using var node = new SluiceNode(cache => cache.WithMemoryCache(memory));
        var sized = new Query<string, string>("stale-sized")
        {
            Size = 3,
            MaxStaleness = TimeSpan.FromMinutes(1),
        };

        await Read(node.Cache, sized);

        memory
            .GetCurrentStatistics()!
            .CurrentEstimatedSize.Should()
            .Be(
                6,
                "the entry and its stale copy each take the query's size; tag markers take none"
            );
        (await HasStaleCopy(node, sized)).Should().BeTrue();
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("a tick under")]
    [InlineData("Timeout.InfiniteTimeSpan")]
    public void MaxStaleness_of_zero_or_less_throws(string value)
    {
        var maxStaleness = value switch
        {
            "zero" => TimeSpan.Zero,
            "a tick under" => TimeSpan.FromTicks(-1),
            _ => Timeout.InfiniteTimeSpan,
        };

        var keyed = () =>
            new Query<string, string>("stale-invalid") { MaxStaleness = maxStaleness };
        var keyless = () =>
            new Query<string>("stale-invalid-keyless") { MaxStaleness = maxStaleness };

        keyed.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("MaxStaleness");
        keyless.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("MaxStaleness");
    }

    [Fact]
    public async Task A_MaxStaleness_that_outlives_the_tag_data_fails_on_first_use_naming_MaxStaleness()
    {
        await using var node = new SluiceNode();
        var longLived = new Query<string, string>("stale-long-lived")
        {
            MaxStaleness = TimeSpan.FromDays(30),
        };

        var call = () => Read(node.Cache, longLived);

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "The MaxStaleness of query 'stale-long-lived' keeps entries in memory*tag data*Shorten MaxStaleness, or*"
            );
        _runs.Should().Be(0);
    }

    [Fact]
    public async Task A_strict_query_is_evicted_at_once_as_before()
    {
        await using var node = new SluiceNode();
        await Read(node.Cache, _strict);
        (await HasStaleCopy(node, _strict)).Should().BeFalse();
        _data[1] = "new";

        await node.Cache.InvalidateAsync(_rows.For(1));

        (await Read(node.Cache, _strict)).Should().Be("new");
        _runs.Should().Be(2);
    }
}
