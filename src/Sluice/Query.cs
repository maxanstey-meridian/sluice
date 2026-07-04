namespace Sluice;

internal sealed class DelegateCachedOperation<TKey, TValue>(
    string name,
    int version,
    Func<TKey, CacheKey> keyFunc,
    Func<TKey, IReadScope, ValueTask<TValue>> computeFunc
) : CachedOperation<TKey, TValue>(name, version)
{
    protected override CacheKey Key(TKey key) => keyFunc(key);

    protected override ValueTask<TValue> Compute(TKey key, OperationContext ctx)
    {
        var scope = new ReadScope(ctx);
        return computeFunc(key, scope);
    }
}

public sealed class Query<TKey, TValue>
{
    private readonly string _name;
    private readonly int _version;
    private Func<TKey, CacheKey>? _keyFunc;
    private Func<TKey, IReadScope, ValueTask<TValue>>? _computeFunc;
    private DelegateCachedOperation<TKey, TValue>? _operation;

    public Query(string name, int version = 1)
    {
        _name = name;
        _version = version;
    }

    public Query<TKey, TValue> Key(Func<TKey, object> keySelector)
    {
        _keyFunc = key => CacheKey.From(keySelector(key));
        return this;
    }

    public Query<TKey, TValue> Compute(Func<TKey, IReadScope, ValueTask<TValue>> compute)
    {
        _computeFunc = compute;
        return this;
    }

    internal CachedOperation<TKey, TValue> Operation =>
        _operation ??= new DelegateCachedOperation<TKey, TValue>(
            _name,
            _version,
            _keyFunc
                ?? throw new InvalidOperationException("Query.Key must be called before execution"),
            _computeFunc
                ?? throw new InvalidOperationException(
                    "Query.Compute must be called before execution"
                )
        );
}
