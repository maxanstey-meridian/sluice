namespace Sluice;

public sealed class EntityResource<TKey>(string name)
    where TKey : IResourceKey
{
    public string Name { get; } = name;

    public ResourceAddress For(TKey key) =>
        new(ResourceKind.Entity, Name, ResourceKeyGuard.RequireRealKey(key.ResourceKey));

    public ResourceAddress Wildcard() => new(ResourceKind.Entity, Name, "*");

    public TrackedRead<TKey, TValue> Read<TValue>(
        Func<TKey, CancellationToken, Task<TValue>> read
    ) => new(For, read);

    public TrackedWrite<TKey> Write(ISluice sluice) => new(sluice, For);
}
