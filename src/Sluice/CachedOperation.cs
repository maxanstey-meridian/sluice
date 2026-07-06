namespace Sluice;

public abstract class CachedOperation<TKey, TValue>(
    string name,
    int version = 1,
    TimeSpan? ttl = null,
    bool allowUntracked = false
) : IOperation
{
    public string Name { get; } = name;

    public int Version { get; } = version;

    internal TimeSpan? Ttl { get; } = ttl;

    internal bool AllowUntracked { get; } = allowUntracked;

    public Type KeyType => typeof(TKey);

    public Type ValueType => typeof(TValue);

    protected abstract CacheKey Key(TKey key);

    protected abstract ValueTask<TValue> Compute(TKey key, OperationContext ctx);

    internal string BuildEntryKey(TKey key) => $"{Name}:v{Version}:{Key(key).Value}";

    internal ValueTask<TValue> RunCompute(TKey key, OperationContext ctx) => Compute(key, ctx);
}
