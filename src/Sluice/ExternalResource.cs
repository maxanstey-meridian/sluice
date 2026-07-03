namespace Sluice;

public sealed class ExternalResource(string name)
{
    public string Name { get; } = name;

    public ResourceAddress For(string key) => new(ResourceKind.External, Name, key);
}
