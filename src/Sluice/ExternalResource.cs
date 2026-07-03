namespace Sluice;

public sealed class ExternalResource
{
    public ExternalResource(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ResourceAddress For(string key)
    {
        return new ResourceAddress(ResourceKind.External, Name, key);
    }
}
