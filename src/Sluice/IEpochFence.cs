namespace Sluice;

public interface IEpochFence
{
    public Task<long> ReadEpochAsync(CancellationToken ct);
    public Task<long> IncrementEpochAsync(
        IReadOnlyList<ResourceAddress> addresses,
        CancellationToken ct
    );
    public Task<bool> HasOverlappingInvalidationAsync(
        long afterEpoch,
        long throughEpoch,
        IReadOnlyList<ResourceAddress> observedReads,
        CancellationToken ct
    );
}
