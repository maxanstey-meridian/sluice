namespace Sluice.Tests;

internal sealed record CustomerId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
    public override string ToString() => Value;
}

internal sealed record OrderId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
    public override string ToString() => Value;
}
