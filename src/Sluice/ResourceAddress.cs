namespace Sluice;

public sealed record ResourceAddress
{
    public ResourceAddress(ResourceKind kind, string name, string key)
    {
        Kind = kind;
        Name = name;
        Key = key;
    }

    public ResourceKind Kind { get; }
    public string Name { get; }
    public string Key { get; }

    public override string ToString() => $"{Kind.ToString().ToLower()}:{Name}:{Key}";
}
