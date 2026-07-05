namespace Sluice;

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
public sealed class SluiceAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReadEntityAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReadCollectionAttribute(string collection, string byKey) : Attribute
{
    public string Collection { get; } = collection;
    public string ByKey { get; } = byKey;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WriteEntityAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WriteCollectionAttribute(string collection, string byKey) : Attribute
{
    public string Collection { get; } = collection;
    public string ByKey { get; } = byKey;
}
