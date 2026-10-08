using ZiggyCreatures.Caching.Fusion;

namespace Sluice;

/// <summary>
/// Entry lifetime must not exceed tag-data lifetime at either level, or an invalidated entry comes back once
/// its tag data expires: from L2 on a fresh node, or from memory on the same node.
/// </summary>
internal static class EntryLifetime
{
    internal static void EnsureWithin(
        FusionCacheEntryOptions entry,
        FusionCacheEntryOptions tags,
        string subject
    )
    {
        // FusionCache jitters memory expiry only. Jitter on the tag side only lengthens tag data, so it's ignored.
        Ensure(
            Jittered(InMemory(entry), entry.JitterMaxDuration),
            InMemory(tags),
            subject,
            "in memory",
            "Duration, MemoryCacheDuration, FailSafeMaxDuration or JitterMaxDuration",
            "TagsDefaultEntryOptions.MemoryCacheDuration or TagsDefaultEntryOptions.FailSafeMaxDuration"
        );
        Ensure(
            InDistributed(entry),
            InDistributed(tags),
            subject,
            "in the distributed cache",
            "Duration, DistributedCacheDuration, DistributedCacheFailSafeMaxDuration or FailSafeMaxDuration",
            "TagsDefaultEntryOptions.DistributedCacheDuration, TagsDefaultEntryOptions.DistributedCacheFailSafeMaxDuration "
                + "or TagsDefaultEntryOptions.FailSafeMaxDuration"
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

    // The worst case of FusionCache's random jitter, saturating at TimeSpan.MaxValue as FusionCache does.
    private static TimeSpan Jittered(TimeSpan lifetime, TimeSpan jitter) =>
        jitter <= TimeSpan.Zero ? lifetime
        : lifetime > TimeSpan.MaxValue - jitter ? TimeSpan.MaxValue
        : lifetime + jitter;

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
