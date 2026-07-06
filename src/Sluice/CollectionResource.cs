namespace Sluice;

public sealed class CollectionResource<TKey>(string name)
    where TKey : IResourceKey
{
    public string Name { get; } = name;

    public ResourceAddress For(TKey key) => new(ResourceKind.Collection, Name, key.ResourceKey);

    public ResourceAddress Wildcard() => new(ResourceKind.Collection, Name, "*");

    public TrackedRead<TKey, TValue> Read<TValue>(
        Func<TKey, CancellationToken, Task<TValue>> read
    ) => new(For, read);
}
