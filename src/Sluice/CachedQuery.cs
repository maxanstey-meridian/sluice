namespace Sluice;

internal sealed class DelegateCachedOperation<TKey, TValue>(
    string name,
    int version,
    Func<TKey, CacheKey> keyFunc,
    Func<TKey, IReadScope, ValueTask<TValue>> computeFunc,
    TimeSpan? ttl = null,
    bool allowUntracked = false
) : CachedOperation<TKey, TValue>(name, version, ttl, allowUntracked)
{
    protected override CacheKey Key(TKey key) => keyFunc(key);

    protected override ValueTask<TValue> Compute(TKey key, OperationContext ctx)
    {
        var scope = new ReadScope(ctx);
        return computeFunc(key, scope);
    }
}

public sealed class CachedQuery<TKey, TValue>(
    string name,
    Func<TKey, object> keySelector,
    Func<TKey, IReadScope, ValueTask<TValue>> compute,
    int version = 1,
    TimeSpan? ttl = null,
    bool allowUntracked = false
)
{
    internal CachedOperation<TKey, TValue> Operation { get; } =
        new DelegateCachedOperation<TKey, TValue>(
            name,
            version,
            key => CacheKey.From(keySelector(key)),
            compute,
            ttl,
            allowUntracked
        );
}
