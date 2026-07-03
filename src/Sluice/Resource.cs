namespace Sluice;

public static class Resource
{
    public static EntityResource<TKey> Entity<TKey>(string name)
        where TKey : IResourceKey => new(name);

    public static CollectionResource<TKey> Collection<TKey>(string name)
        where TKey : IResourceKey => new(name);

    public static ExternalResource External(string name) => new(name);
}
