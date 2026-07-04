namespace Sluice;

/// <summary>
/// Marker interface for resource key types. The <see cref="ResourceKey"/> value
/// becomes part of the resource address string and must satisfy these contracts:
/// <list type="bullet">
///   <item>Must be stable (does not change over time for the same logical key).</item>
///   <item>Must be culture-invariant.</item>
///   <item>Must be unique within its resource type.</item>
///   <item>Must NOT contain ':' (the address delimiter).</item>
///   <item>Must NOT be '*' (reserved for wildcard addresses).</item>
/// </list>
/// </summary>
public interface IResourceKey
{
    /// <summary>
    /// A stable, culture-invariant string that uniquely identifies the key within
    /// its resource type. Must NOT contain ':' (the address delimiter). Must NOT be
    /// '*' (reserved for wildcard addresses).
    /// </summary>
    public string ResourceKey { get; }
}
