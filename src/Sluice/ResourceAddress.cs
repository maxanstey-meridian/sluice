namespace Sluice;

/// <summary>
/// A resource address uniquely identifies a cache entry by combining a resource
/// kind, a resource name, and a key. The address string uses ':' as the delimiter
/// in the format {kind}:{name}:{key}.
/// </summary>
/// <remarks>
/// The <see cref="Key"/> value '*' is reserved as a wildcard sentinel — it means
/// 'match all keys' and is used by wildcard invalidation methods. Real key values
/// must not be '*'.
/// </remarks>
public sealed record ResourceAddress
{
    public ResourceKind Kind { get; }
    public string Name { get; }
    public string Key { get; }

    public ResourceAddress(ResourceKind kind, string name, string key)
    {
        if (name.Contains(':'))
        {
            throw new ArgumentException(
                "Resource name cannot contain ':' — it is the address delimiter.",
                nameof(name));
        }

        if (key.Contains(':'))
        {
            throw new ArgumentException(
                "Resource key cannot contain ':' — it is the address delimiter.",
                nameof(key));
        }

        Kind = kind;
        Name = name;
        Key = key;
    }

    public override string ToString() => $"{Kind.ToString().ToLower()}:{Name}:{Key}";
}
