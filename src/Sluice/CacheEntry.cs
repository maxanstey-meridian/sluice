namespace Sluice;

public sealed record CacheEntry<TValue>(
    TValue Value,
    IReadOnlyList<ResourceAddress> ObservedReads,
    DateTimeOffset CachedAt,
    DateTimeOffset? ExpiresAt = null
);
