namespace Sluice;

public sealed class EntityResource<TKey>(string name)
    where TKey : IResourceKey
{
    public string Name { get; } = name;

    public ResourceAddress For(TKey key) => new(ResourceKind.Entity, Name, key.ToString()!);

    public ResourceAddress Wildcard() => new(ResourceKind.Entity, Name, "*");
}
