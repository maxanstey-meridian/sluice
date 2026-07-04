namespace Sluice;

internal sealed class DelegateCachedOperation<TKey, TValue>(
    string name,
    int version,
    Func<TKey, CacheKey> keyFunc,
    Func<TKey, IReadScope, ValueTask<TValue>> computeFunc,
    TimeSpan? ttl = null
) : CachedOperation<TKey, TValue>(name, version, ttl)
{
    protected override CacheKey Key(TKey key) => keyFunc(key);

    protected override ValueTask<TValue> Compute(TKey key, OperationContext ctx)
    {
        var scope = new ReadScope(ctx);
        return computeFunc(key, scope);
    }
}

public sealed class Query<TKey, TValue>(
    string name,
    Func<TKey, object> keySelector,
    Func<TKey, IReadScope, ValueTask<TValue>> compute,
    int version = 1,
    TimeSpan? ttl = null
)
{
    internal CachedOperation<TKey, TValue> Operation { get; } =
        new DelegateCachedOperation<TKey, TValue>(
            name,
            version,
            key => CacheKey.From(keySelector(key)),
            compute,
            ttl
        );
}
