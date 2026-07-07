namespace Sluice.Redis.Tests;

internal sealed record StringKey(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}
