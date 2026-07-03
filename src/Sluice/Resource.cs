namespace Sluice;

public static class Resource
{
    public static EntityResource<TKey> Entity<TKey>(string name)
        where TKey : IResourceKey
    {
        return new EntityResource<TKey>(name);
    }

    public static CollectionResource<TKey> Collection<TKey>(string name)
        where TKey : IResourceKey
    {
        return new CollectionResource<TKey>(name);
    }

    public static ExternalResource External(string name)
    {
        return new ExternalResource(name);
    }
}
