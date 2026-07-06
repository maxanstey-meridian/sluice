namespace Sluice;

public interface IGraphStore
{
    public Task ClearEntryEdges(string entryKey, CancellationToken ct);
    public Task RecordEntry(
        string entryKey,
        IReadOnlyList<ResourceAddress> addresses,
        DateTimeOffset cachedAt,
        CancellationToken ct
    );
    public Task<IReadOnlyList<string>> FindAffectedEntries(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    );
    public Task FlushAsync(CancellationToken ct);
    public Task<string> DumpGraphAsync(CancellationToken ct);
}
