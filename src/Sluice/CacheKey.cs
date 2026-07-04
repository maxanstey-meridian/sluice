namespace Sluice;

/// <summary>
/// The serialized cache key value used in entry keys. Entry keys have the format
/// {operationName}:v{version}:{cacheKey.Value} and are used as opaque dictionary
/// keys — they are never parsed back.
/// </summary>
/// <remarks>
/// The <see cref="Value"/> may contain ':' from JSON serialization (e.g., JSON
/// property separators). This is safe because no code splits on ':' in entry keys.
/// Entry keys are opaque dictionary keys, not delimiter-structured addresses.
/// </remarks>
public sealed record CacheKey(string Value)
{
    public static CacheKey From(object keyShape) =>
        new(System.Text.Json.JsonSerializer.Serialize(keyShape));

    public override string ToString() => Value;
}
