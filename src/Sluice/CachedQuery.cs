namespace Sluice;

internal sealed class DelegateCachedOperation<TKey, TValue>(
    string name,
    int version,
    Func<TKey, IReadScope, ValueTask<TValue>> computeFunc,
    TimeSpan? ttl = null,
    bool allowUntracked = false
) : CachedOperation<TKey, TValue>(name, version, ttl, allowUntracked)
    where TKey : IResourceKey
{
    protected override CacheKey Key(TKey key) => CacheKey.From(key.ResourceKey);

    protected override ValueTask<TValue> Compute(TKey key, OperationContext ctx)
    {
        var scope = new ReadScope(ctx);
        return computeFunc(key, scope);
    }
}

public sealed class CachedQuery<TKey, TValue>(
    string name,
    Func<TKey, IReadScope, ValueTask<TValue>> compute,
    int version = 1,
    TimeSpan? ttl = null,
    bool allowUntracked = false
)
    where TKey : IResourceKey
{
    internal CachedOperation<TKey, TValue> Operation { get; } =
        new DelegateCachedOperation<TKey, TValue>(name, version, compute, ttl, allowUntracked);
}
