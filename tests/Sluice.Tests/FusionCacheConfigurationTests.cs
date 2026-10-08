using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.Tests;

public sealed class FusionCacheConfigurationTests
{
    private static readonly Source<int> _rows = new("config-rows");
    private static readonly Query<string, string> _view = new("config-view");

    private static Task<string> Read(SluiceReads read, CancellationToken ct)
    {
        read.From(_rows.For(1));
        return Task.FromResult("value");
    }

    private static Task<string> Failing(SluiceReads read, CancellationToken ct) =>
        throw new TimeoutException("database down");

    // A user who asks for FusionCache's default Expire behaviour, which Sluice must override.
    private static void FailSafeWithExpire(IFusionCacheBuilder cache) =>
        cache
            .WithDefaultEntryOptions(o => o.IsFailSafeEnabled = true)
            .WithOptions(o => o.RemoveByTagBehavior = RemoveByTagBehavior.Expire);

    // T13
    [Fact]
    public async Task With_fail_safe_on_a_failing_compute_after_invalidation_throws_instead_of_serving_the_invalidated_value()
    {
        await using var node = new SluiceNode(FailSafeWithExpire);
        await node.Cache.GetOrSetAsync(_view, "k", Read);
        await node.Cache.InvalidateAsync(_rows.For(1));

        var call = () => node.Cache.GetOrSetAsync(_view, "k", Failing).AsTask();

        await call.Should().ThrowAsync<TimeoutException>();
    }

    // T13
    [Fact]
    public async Task With_fail_safe_on_a_failing_compute_after_ClearAsync_throws_instead_of_serving_the_cleared_value()
    {
        await using var node = new SluiceNode(FailSafeWithExpire);
        await node.Cache.GetOrSetAsync(_view, "k", Read);
        await node.Cache.ClearAsync();

        var call = () => node.Cache.GetOrSetAsync(_view, "k", Failing).AsTask();

        await call.Should().ThrowAsync<TimeoutException>();
    }

    // T13
    [Fact]
    public async Task A_user_who_disables_tagging_still_gets_invalidation()
    {
        await using var node = new SluiceNode(cache =>
            cache.WithOptions(o => o.DisableTagging = true)
        );
        var reader = new CountingReader(node.Cache, _view);
        await reader.GetAsync("k", _rows.For(1));

        await node.Cache.InvalidateAsync(_rows.For(1));
        await reader.GetAsync("k", _rows.For(1));

        reader.Runs("k").Should().Be(2);
    }

    // T15
    [Fact]
    public void Resolving_SluiceCache_fails_when_the_default_duration_outlives_the_tag_data()
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithDefaultEntryOptions(o => o.Duration = TimeSpan.FromDays(30))
            );

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*DefaultEntryOptions*in memory*tag data*");
    }

    // T22
    [Fact]
    public void Resolving_SluiceCache_fails_when_the_distributed_duration_outlives_the_tag_data()
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithDefaultEntryOptions(o =>
                    o.DistributedCacheDuration = TimeSpan.FromDays(30)
                )
            );

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*in the distributed cache*tag data*");
    }

    // T22
    [Fact]
    public void Resolving_SluiceCache_fails_when_the_distributed_fail_safe_lifetime_outlives_the_tag_data()
    {
        // Memory keeps entries for FailSafeMaxDuration (one day by default), well within the tag data.
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithDefaultEntryOptions(o =>
                {
                    o.IsFailSafeEnabled = true;
                    o.DistributedCacheFailSafeMaxDuration = TimeSpan.FromDays(30);
                })
            );

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*in the distributed cache*tag data*");
    }

    // T22
    [Fact]
    public void Resolving_SluiceCache_fails_when_the_fail_safe_lifetime_outlives_the_tag_data()
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithDefaultEntryOptions(o =>
                {
                    o.IsFailSafeEnabled = true;
                    o.FailSafeMaxDuration = TimeSpan.FromDays(30);
                })
            );

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*in memory*tag data*");
    }

    // T22
    [Fact]
    public void Resolving_SluiceCache_fails_when_the_memory_duration_outlives_the_tag_data()
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithDefaultEntryOptions(o => o.MemoryCacheDuration = TimeSpan.FromDays(30))
            );

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*in memory*tag data*");
    }

    // T22
    [Fact]
    public void Resolving_SluiceCache_fails_when_memory_jitter_can_push_an_entry_past_the_tag_data()
    {
        // Entry and tag data both last an hour; FusionCache adds up to a second of jitter to the entry in memory.
        var resolve = () =>
            new SluiceNode(cache =>
                cache
                    .WithDefaultEntryOptions(o =>
                    {
                        o.Duration = TimeSpan.FromHours(1);
                        o.JitterMaxDuration = TimeSpan.FromSeconds(1);
                    })
                    .WithOptions(o =>
                    {
                        o.TagsDefaultEntryOptions.Duration = TimeSpan.FromHours(1);
                        o.TagsDefaultEntryOptions.IsFailSafeEnabled = false;
                    })
            );

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*in memory*tag data*JitterMaxDuration*");
    }

    // T22
    [Fact]
    public async Task Resolving_SluiceCache_succeeds_when_the_tag_data_is_lengthened_to_match()
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache
                    .WithDefaultEntryOptions(o =>
                    {
                        o.MemoryCacheDuration = TimeSpan.FromDays(30);
                        o.DistributedCacheDuration = TimeSpan.FromDays(30);
                    })
                    .WithOptions(o =>
                        o.TagsDefaultEntryOptions.FailSafeMaxDuration = TimeSpan.FromDays(31)
                    )
            );

        await using var node = resolve.Should().NotThrow().Subject;
    }

    // T22
    [Fact]
    public async Task A_query_whose_duration_outlives_the_tag_data_fails_on_first_use()
    {
        await using var node = new SluiceNode();
        var longLived = new Query<string, string>("config-long-lived")
        {
            Duration = TimeSpan.FromDays(30),
        };
        var ran = false;

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    longLived,
                    "k",
                    (read, ct) =>
                    {
                        ran = true;
                        return Read(read, ct);
                    }
                )
                .AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*config-long-lived*in memory*tag data*");
        ran.Should().BeFalse();
    }

    // T16
    [Fact]
    public async Task Cancelling_the_caller_cancels_the_compute()
    {
        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var call = node
            .Cache.GetOrSetAsync(
                _view,
                "k",
                async (read, ct) =>
                {
                    read.From(_rows.For(1));
                    using var registration = ct.Register(() => cancelled.TrySetResult());
                    started.SetResult();
                    await cancelled.Task;
                    ct.ThrowIfCancellationRequested();
                    return "never";
                },
                caller.Token
            )
            .AsTask();
        await started.Task;
        await caller.CancelAsync();

        // The bound only turns a regression into a failure instead of a hang.
        await call.Invoking(c => c.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    // T16
    [Fact]
    public async Task An_already_cancelled_caller_gets_neither_a_hit_nor_a_compute()
    {
        await using var node = new SluiceNode();
        await node.Cache.GetOrSetAsync(_view, "cached", Read);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var ran = false;

        Task<string> Compute(SluiceReads read, CancellationToken ct)
        {
            ran = true;
            return Read(read, ct);
        }

        var hit = () => node.Cache.GetOrSetAsync(_view, "cached", Compute, caller.Token).AsTask();
        var miss = () => node.Cache.GetOrSetAsync(_view, "missing", Compute, caller.Token).AsTask();

        await hit.Should().ThrowAsync<OperationCanceledException>();
        await miss.Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();
    }

    // T32
    [Fact]
    public async Task A_cancelled_caller_resumes_only_after_its_compute_ends()
    {
        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;

        var call = node
            .Cache.GetOrSetAsync(
                _view,
                "k",
                async (read, ct) =>
                {
                    read.From(_rows.For(1));
                    started.SetResult();

                    // A step that doesn't stop on cancellation, like a query finishing on the caller's connection.
                    await gate.Task;
                    ended = true;
                    ct.ThrowIfCancellationRequested();
                    return "never";
                },
                caller.Token
            )
            .AsTask();

        // What the caller would find the moment it resumes.
        var endedWhenCallerResumed = call.ContinueWith(
            _ => ended,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        await started.Task;
        await caller.CancelAsync();

        call.IsCompleted.Should().BeFalse("the caller must wait for its compute");
        gate.SetResult();
        (await endedWhenCallerResumed).Should().BeTrue();
        await call.Invoking(c => c).Should().ThrowAsync<OperationCanceledException>();
    }

    // T24
    [Fact]
    public async Task A_compute_outliving_the_factory_hard_timeout_completes_before_the_caller_resumes()
    {
        // FusionCache times a zero hard timeout out at once, before the factory has finished (or even run).
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o => o.FactoryHardTimeout = TimeSpan.Zero)
        );
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        var call = node
            .Cache.GetOrSetAsync(
                _view,
                "k",
                async (read, _) =>
                {
                    read.From(_rows.For(1));
                    started.SetResult();
                    await gate.Task;
                    finished = true;
                    return "value";
                }
            )
            .AsTask();

        // Without the pin FusionCache times the call out before the compute starts, so wait for either.
        await Task.WhenAny(started.Task, call);
        call.IsCompleted.Should().BeFalse("the caller must wait for its compute");
        gate.SetResult();
        (await call).Should().Be("value");
        finished.Should().BeTrue();
    }

    // T24
    [Fact]
    public async Task A_compute_outliving_the_factory_soft_timeout_completes_before_the_caller_resumes()
    {
        // A soft timeout applies when fail-safe has a stale value to fall back to; zero times out at once.
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o =>
            {
                o.IsFailSafeEnabled = true;
                o.FactorySoftTimeout = TimeSpan.Zero;
            })
        );
        await node.Cache.GetOrSetAsync(_view, "k", Read);
        await node.Fusion.ExpireAsync("config-view:k");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = node
            .Cache.GetOrSetAsync(
                _view,
                "k",
                async (read, _) =>
                {
                    read.From(_rows.For(1));
                    started.SetResult();
                    await gate.Task;
                    return "new value";
                }
            )
            .AsTask();

        // Without the pin FusionCache serves the stale value at once, so wait for either.
        await Task.WhenAny(started.Task, call);
        call.IsCompleted.Should().BeFalse("the caller must wait for its compute");
        gate.SetResult();
        (await call).Should().Be("new value");
    }

    // T21
    [Fact]
    public async Task Another_FusionCache_on_the_same_L2_is_unaffected_by_Sluice_tags_and_clear()
    {
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using var node = new SluiceNode(cache =>
            cache.WithSerializer(new FusionCacheSystemTextJsonSerializer()).WithDistributedCache(l2)
        );
        using var other = new FusionCache(new FusionCacheOptions { CacheName = "other" });
        other.SetupDistributedCache(l2, new FusionCacheSystemTextJsonSerializer());

        // The tags Sluice reads and publishes for _rows.For(1).
        await other.SetAsync(
            "tagged",
            "kept",
            tags: ["config-rows:1", "config-rows:*", "config-rows:"]
        );
        await other.SetAsync("plain", "kept");

        await node.Cache.InvalidateAsync([_rows.For(1), _rows.All]);
        await node.Cache.ClearAsync();

        (await other.TryGetAsync<string>("tagged")).GetValueOrDefault().Should().Be("kept");
        (await other.TryGetAsync<string>("plain")).GetValueOrDefault().Should().Be("kept");
    }
}
