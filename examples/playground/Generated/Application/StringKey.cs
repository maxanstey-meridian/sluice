using Sluice;

namespace Playground.Generated.Application;

public sealed record StringKey(string Value) : IResourceKey
{
    // ResourceKey is the stable string Sluice stores in resource addresses.
    // For this demo the user/flag/greeting IDs are already stable strings.
    public string ResourceKey => Value;
}
