namespace Sluice;

public sealed class CollectionResource<TKey>
    where TKey : IResourceKey
{
    public CollectionResource(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ResourceAddress For(TKey key)
    {
        return new ResourceAddress(ResourceKind.Collection, Name, key.ToString()!);
    }
}
