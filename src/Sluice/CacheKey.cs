namespace Sluice;

public sealed record CacheKey(string Value)
{
    public static CacheKey From(object keyShape) =>
        new(System.Text.Json.JsonSerializer.Serialize(keyShape));

    public override string ToString() => Value;
}
