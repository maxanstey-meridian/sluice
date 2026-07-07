using Sluice;

namespace Playground.Manual.Domain;

public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;

    public static implicit operator UserId(string value) => new(value);
}

public sealed record FeatureFlagId(string Value) : IResourceKey
{
    public static readonly FeatureFlagId DarkMode = new("dark_mode");

    public string ResourceKey => Value;

    public static implicit operator FeatureFlagId(string value) => new(value);
}
