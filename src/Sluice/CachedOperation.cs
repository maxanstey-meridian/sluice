namespace Sluice;

public abstract class CachedOperation<TKey, TValue>(string name, int version = 1)
{
    public string Name { get; } = name;

    public int Version { get; } = version;

    protected abstract CacheKey Key(TKey key);

    protected abstract ValueTask<TValue> Compute(TKey key, OperationContext ctx);

    internal string BuildEntryKey(TKey key) => $"{Name}:v{Version}:{Key(key).Value}";

    internal ValueTask<TValue> RunCompute(TKey key, OperationContext ctx) => Compute(key, ctx);
}
