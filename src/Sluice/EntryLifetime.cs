using ZiggyCreatures.Caching.Fusion;

namespace Sluice;

/// <summary>
/// Entry lifetime must not exceed tag-data lifetime at either level, or an invalidated entry comes back once
/// its tag data expires: from L2 on a fresh node, or from memory on the same node (DESIGN §5.4, D15, research/01 §3).
/// </summary>
internal static class EntryLifetime
{
    internal static void EnsureWithin(
        FusionCacheEntryOptions entry,
        FusionCacheEntryOptions tags,
        string subject
    )
    {
        Ensure(
            InMemory(entry),
            InMemory(tags),
            subject,
            "in memory",
            "Duration, MemoryCacheDuration or FailSafeMaxDuration",
            "TagsDefaultEntryOptions.MemoryCacheDuration or TagsDefaultEntryOptions.FailSafeMaxDuration"
        );
        Ensure(
            InDistributed(entry),
            InDistributed(tags),
            subject,
            "in the distributed cache",
            "Duration, DistributedCacheDuration or FailSafeMaxDuration",
            "TagsDefaultEntryOptions.DistributedCacheDuration or TagsDefaultEntryOptions.FailSafeMaxDuration"
        );
    }

    // FusionCache's own physical lifetimes: FusionCacheEntryOptions.GetMemoryAbsoluteExpiration
    // and FusionCacheEntryOptions.ToDistributedCacheEntryOptions.
    private static TimeSpan InMemory(FusionCacheEntryOptions options) =>
        Physical(
            options,
            options.MemoryCacheDuration ?? options.Duration,
            options.FailSafeMaxDuration
        );

    private static TimeSpan InDistributed(FusionCacheEntryOptions options) =>
        Physical(
            options,
            options.DistributedCacheDuration ?? options.Duration,
            options.DistributedCacheFailSafeMaxDuration ?? options.FailSafeMaxDuration
        );

    private static TimeSpan Physical(
        FusionCacheEntryOptions options,
        TimeSpan logical,
        TimeSpan failSafe
    ) => options.IsFailSafeEnabled && failSafe > logical ? failSafe : logical;

    private static void Ensure(
        TimeSpan lifetime,
        TimeSpan tagLifetime,
        string subject,
        string level,
        string shorten,
        string lengthen
    )
    {
        if (lifetime > tagLifetime)
        {
            throw new InvalidOperationException(
                $"{subject} keeps entries {level} for {lifetime}, longer than Sluice's tag data there ({tagLifetime}), "
                    + $"so an invalidated entry could come back once its tag data expires. Shorten {shorten}, or lengthen {lengthen}."
            );
        }
    }
}
