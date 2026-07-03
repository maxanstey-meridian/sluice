namespace Sluice;

public sealed class EntityResource<TKey>
    where TKey : IResourceKey
{
    public EntityResource(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ResourceAddress For(TKey key)
    {
        return new ResourceAddress(ResourceKind.Entity, Name, key.ToString()!);
    }
}
