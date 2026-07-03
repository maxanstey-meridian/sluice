namespace Sluice;

public sealed record ResourceAddress(ResourceKind Kind, string Name, string Key)
{
    public override string ToString() => $"{Kind.ToString().ToLower()}:{Name}:{Key}";
}
