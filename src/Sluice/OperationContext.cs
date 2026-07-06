namespace Sluice;

public sealed class OperationContext(TimeProvider clock, CancellationToken cancellationToken)
{
    private readonly List<ResourceAddress> _observedReads = [];

    public IReadOnlyList<ResourceAddress> ObservedReads => _observedReads;

    public TimeProvider Clock { get; } = clock;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public void RecordRead(ResourceAddress address) => _observedReads.Add(address);
}
