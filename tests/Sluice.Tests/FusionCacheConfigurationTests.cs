using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.Tests;

public sealed class FusionCacheConfigurationTests
{
    private static readonly Source<int> _rows = new("config-rows");
    private static readonly Query<string, string> _view = new("config-view");
    private static readonly Query<string, string> _staleView = new("config-stale-view")
    {
        MaxStaleness = TimeSpan.FromMinutes(1),
    };

    // Cache configurations whose entries outlive the tag data that can invalidate them, each with the message
    // resolving SluiceCache must fail with.
    private static readonly Dictionary<
        string,
        (Action<IFusionCacheBuilder> Configure, string Message)
    > _outlivingTagData = new()
    {
        ["default duration"] = (
            cache => cache.WithDefaultEntryOptions(o => o.Duration = TimeSpan.FromDays(30)),
            "*DefaultEntryOptions*in memory*tag data*"
        ),
        ["distributed duration"] = (
            cache =>
                cache.WithDefaultEntryOptions(o =>
                    o.DistributedCacheDuration = TimeSpan.FromDays(30)
                ),
            "*in the distributed cache*tag data*"
        ),
        // Memory keeps entries for FailSafeMaxDuration (one day by default), well within the tag data.
        ["distributed fail-safe lifetime"] = (
            cache =>
                cache.WithDefaultEntryOptions(o =>
                {
                    o.IsFailSafeEnabled = true;
                    o.DistributedCacheFailSafeMaxDuration = TimeSpan.FromDays(30);
                }),
            "*in the distributed cache*tag data*"
        ),
        ["fail-safe lifetime"] = (
            cache =>
                cache.WithDefaultEntryOptions(o =>
                {
                    o.IsFailSafeEnabled = true;
                    o.FailSafeMaxDuration = TimeSpan.FromDays(30);
                }),
            "*in memory*tag data*"
        ),
        ["memory duration"] = (
            cache =>
                cache.WithDefaultEntryOptions(o => o.MemoryCacheDuration = TimeSpan.FromDays(30)),
            "*in memory*tag data*"
        ),
        // Entry and tag data both last an hour; FusionCache adds up to a second of jitter to the entry in memory.
        ["memory jitter"] = (
            cache =>
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
                    }),
            "*in memory*tag data*JitterMaxDuration*"
        ),
    };

    public static TheoryData<string> OutlivingTagData => new(_outlivingTagData.Keys);

    // A strict query, or one with MaxStaleness, whose calls go through the stale copy's FusionCache call first.
    private static Query<string, string> QueryOf(string kind) =>
        kind == "MaxStaleness" ? _staleView : _view;

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

    [Fact]
    public async Task With_fail_safe_on_a_failing_compute_after_invalidation_throws_instead_of_serving_the_invalidated_value()
    {
        await using var node = new SluiceNode(FailSafeWithExpire);
        await node.Cache.GetOrSetAsync(_view, "k", Read);
        await node.Cache.InvalidateAsync(_rows.For(1));

        var call = () => node.Cache.GetOrSetAsync(_view, "k", Failing).AsTask();

        await call.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task With_fail_safe_on_a_failing_compute_after_ClearAsync_throws_instead_of_serving_the_cleared_value()
    {
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o => o.IsFailSafeEnabled = true)
        );
        await node.Cache.GetOrSetAsync(_view, "k", Read);
        await node.Cache.ClearAsync();

        var call = () => node.Cache.GetOrSetAsync(_view, "k", Failing).AsTask();

        await call.Should().ThrowAsync<TimeoutException>();
    }

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

    [Theory]
    [MemberData(nameof(OutlivingTagData))]
    public void Resolving_SluiceCache_fails_when_an_entry_can_outlive_the_tag_data(string lifetime)
    {
        var (configure, message) = _outlivingTagData[lifetime];

        var resolve = () => new SluiceNode(configure);

        resolve.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

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

    // FusionCache keeps a tag's data at "<prefix><tag>" among the entries and skips the tag check of any key under the
    // prefix: under "t:", query "t" key "x" is tag "x"'s marker. Query names can't start with "__fc", so a prefix that
    // does keeps every entry key out of it.
    [Theory]
    [InlineData("set directly")]
    [InlineData("SetToSafeStrings with another special character")]
    [InlineData("blank")]
    public void Resolving_SluiceCache_fails_when_the_tag_key_prefix_is_outside_the_reserved_prefix(
        string setup
    )
    {
        var (configure, prefix) = setup switch
        {
            "set directly" => (
                (Action<FusionCacheOptions>)(o => o.InternalStrings.TagCacheKeyPrefix = "t:"),
                "t:"
            ),
            "blank" => (o => o.InternalStrings.TagCacheKeyPrefix = "", ""),
            _ => (o => o.InternalStrings.SetToSafeStrings(specialChar: '-'), "--fc-t-"),
        };

        var resolve = () => new SluiceNode(cache => cache.WithOptions(configure));

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"The Sluice cache's InternalStrings.TagCacheKeyPrefix is '{prefix}', which doesn't start with '__fc'*"
                    + "SetToSafeStrings with '_' as its special character."
            );
    }

    [Fact]
    public async Task Resolving_SluiceCache_succeeds_with_SetToSafeStrings_defaults()
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithOptions(o =>
                {
                    o.InternalStrings.SetToSafeStrings();

                    // A ':' with nothing before it can't follow a source name.
                    o.InternalStrings.ClearRemoveTag = ":rem";
                })
            );

        await using var node = resolve.Should().NotThrow().Subject;
        var reader = new CountingReader(node.Cache, _view);
        await reader.GetAsync("k", _rows.For(1));
        await node.Cache.InvalidateAsync(_rows.For(1));
        await node.Cache.ClearAsync();
        await reader.GetAsync("k", _rows.For(1));
        reader.Runs("k").Should().Be(2);
    }

    // Invalidating source "a" key "b" publishes the tag "a:b", which FusionCache would take for a clear on every node.
    [Theory]
    [InlineData("ClearRemoveTag", "a:b", "a")]
    [InlineData("ClearExpireTag", "a:", "a")]
    [InlineData("ClearRemoveTag", "a:b:*", "a:b")]
    public void Resolving_SluiceCache_fails_when_a_clear_tag_could_be_a_dependency_tag(
        string option,
        string tag,
        string source
    )
    {
        var resolve = () =>
            new SluiceNode(cache =>
                cache.WithOptions(o =>
                {
                    if (option == "ClearRemoveTag")
                    {
                        o.InternalStrings.ClearRemoveTag = tag;
                    }
                    else
                    {
                        o.InternalStrings.ClearExpireTag = tag;
                    }
                })
            );

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"The Sluice cache's InternalStrings.{option} is '{tag}', which could be the tag Sluice uses for source "
                    + $"'{source}', so invalidating that source would clear the cache on every node.*"
            );
    }

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

    [Theory]
    [InlineData("strict")]
    [InlineData("MaxStaleness")]
    public async Task An_already_cancelled_caller_gets_neither_a_hit_nor_a_compute(string kind)
    {
        var query = QueryOf(kind);
        await using var node = new SluiceNode();
        await node.Cache.GetOrSetAsync(query, "cached", Read);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var ran = false;

        Task<string> Compute(SluiceReads read, CancellationToken ct)
        {
            ran = true;
            return Read(read, ct);
        }

        var hit = () => node.Cache.GetOrSetAsync(query, "cached", Compute, caller.Token).AsTask();
        var miss = () => node.Cache.GetOrSetAsync(query, "missing", Compute, caller.Token).AsTask();

        await hit.Should().ThrowAsync<OperationCanceledException>();
        await miss.Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse();
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("MaxStaleness")]
    public async Task A_cancelled_caller_resumes_only_after_its_compute_ends(string kind)
    {
        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;

        var call = node
            .Cache.GetOrSetAsync(
                QueryOf(kind),
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

        // A caller that doesn't wait resumes on another thread once the cancellation reaches FusionCache, so give it
        // the time to.
        await Task.WhenAny(call, Task.Delay(TimeSpan.FromMilliseconds(200)));
        call.IsCompleted.Should().BeFalse("the caller must wait for its compute");
        gate.SetResult();
        (await endedWhenCallerResumed).Should().BeTrue();
        await call.Invoking(c => c).Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false, "strict")]
    [InlineData(true, "strict")]
    [InlineData(false, "MaxStaleness")]
    [InlineData(true, "MaxStaleness")]
    public async Task A_compute_outliving_the_factory_timeout_completes_before_the_caller_resumes(
        bool soft,
        string kind
    )
    {
        var query = QueryOf(kind);

        // FusionCache times a zero timeout out at once, before the factory has finished (or even run): a hard one
        // always, a soft one when fail-safe has a stale value to fall back to.
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o =>
            {
                if (soft)
                {
                    o.IsFailSafeEnabled = true;
                    o.FactorySoftTimeout = TimeSpan.Zero;
                }
                else
                {
                    o.FactoryHardTimeout = TimeSpan.Zero;
                }
            })
        );
        if (soft)
        {
            // The stale copy goes too, or it would be served without a compute.
            await node.Cache.GetOrSetAsync(query, "k", Read);
            await node.Fusion.ExpireAsync(query.EntryKey("k"));
            await node.Fusion.RemoveAsync($":{query.EntryKey("k")}");
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        var call = node
            .Cache.GetOrSetAsync(
                query,
                "k",
                async (read, _) =>
                {
                    read.From(_rows.For(1));
                    started.SetResult();
                    await gate.Task;
                    finished = true;
                    return "new value";
                }
            )
            .AsTask();

        // Without the pin FusionCache times the call out (hard) or serves the stale value (soft) before the compute
        // finishes, maybe before it starts, so wait for either.
        await Task.WhenAny(started.Task, call);
        call.IsCompleted.Should().BeFalse("the caller must wait for its compute");
        gate.SetResult();
        (await call).Should().Be("new value");
        finished.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_FusionCache_on_the_same_L2_is_unaffected_by_Sluice_tags_and_clear(
        bool userTurnsThePrefixOff
    )
    {
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using var node = new SluiceNode(cache =>
        {
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(l2);
            if (userTurnsThePrefixOff)
            {
                // Sluice's prefix must win, or its tags and clear would hit the other cache's unprefixed keys.
                cache.WithoutCacheKeyPrefix();
            }
        });
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

    [Fact]
    public void Registering_Sluice_twice_throws()
    {
        var services = new ServiceCollection().AddSluice();

        var again = () => services.AddSluice();

        again
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*AddSluice was called more than once*");
    }

    [Fact]
    public async Task A_query_s_size_is_its_entry_s_size_in_a_memory_cache_with_a_size_limit()
    {
        using var memory = new MemoryCache(
            new MemoryCacheOptions { SizeLimit = 10, TrackStatistics = true }
        );
        await using var node = new SluiceNode(cache => cache.WithMemoryCache(memory));
        var reader = new CountingReader(
            node.Cache,
            new Query<string, string>("config-sized") { Size = 3 }
        );

        await reader.GetAsync("k", _rows.For(1));
        await reader.GetAsync("k", _rows.For(1));

        reader.Runs("k").Should().Be(1, "the sized entry was stored");
        memory
            .GetCurrentStatistics()!
            .CurrentEstimatedSize.Should()
            .Be(3, "tag markers take no room");
        await node.Cache.InvalidateAsync(_rows.For(1));
        await reader.GetAsync("k", _rows.For(1));
        reader.Runs("k").Should().Be(2);
    }

    [Fact]
    public void A_negative_query_size_throws()
    {
        var declare = () => new Query<string>("config-negative-size") { Size = -1 };

        declare.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Under a size limit, a tag marker compacted away, or refused for want of room, reads as never invalidated, so
    // the invalidated value would be served again. The values here can't be evicted, and the app asks for markers that
    // can, or that take room: Sluice pins them whatever the app asks.
    [Theory]
    [InlineData("compacted")]
    [InlineData("no room")]
    public async Task Under_a_size_limit_an_invalidated_value_never_comes_back(string pressure)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 4 });
        await using var node = new SluiceNode(cache =>
            cache
                .WithMemoryCache(memory)
                .WithDefaultEntryOptions(o =>
                {
                    o.Size = 1;
                    o.Priority = CacheItemPriority.NeverRemove;
                })
                .WithOptions(o =>
                {
                    o.TagsDefaultEntryOptions.Priority = CacheItemPriority.Low;
                    o.TagsDefaultEntryOptions.Size = 1;
                })
        );
        var reader = new CountingReader(node.Cache, new Query<string, string>("config-pressure"));
        await reader.GetAsync("k", _rows.For(1));
        if (pressure == "no room")
        {
            // Fills the cache, so a marker that takes room has none.
            for (var key = 0; key < 3; key++)
            {
                await reader.GetAsync($"filler-{key}", new Source<int>("config-filler").For(key));
            }
        }

        await node.Cache.InvalidateAsync(_rows.For(1));
        if (pressure == "compacted")
        {
            memory.Compact(1.0);
        }

        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2");
    }
}
