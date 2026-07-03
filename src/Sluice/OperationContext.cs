namespace Sluice;

public sealed class OperationContext
{
    private readonly List<ResourceAddress> _observedReads = new();

    public IReadOnlyList<ResourceAddress> ObservedReads => _observedReads;

    public TimeProvider Clock { get; }

    public CancellationToken CancellationToken { get; }

    public OperationContext()
    {
        Clock = TimeProvider.System;
        CancellationToken = CancellationToken.None;
    }

    public OperationContext(CancellationToken cancellationToken)
    {
        Clock = TimeProvider.System;
        CancellationToken = cancellationToken;
    }

    public OperationContext(TimeProvider clock, CancellationToken cancellationToken)
    {
        Clock = clock;
        CancellationToken = cancellationToken;
    }

    internal void RecordRead(ResourceAddress address)
    {
        _observedReads.Add(address);
    }
}
