namespace Sluice;

public sealed class CollectionResource<TKey>(string name)
    where TKey : IResourceKey
{
    public string Name { get; } = name;

    public ResourceAddress For(TKey key) => new(ResourceKind.Collection, Name, key.ToString()!);
}
