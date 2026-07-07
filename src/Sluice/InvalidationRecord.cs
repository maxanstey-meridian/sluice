namespace Sluice;

public sealed record InvalidationRecord(long Epoch, IReadOnlyList<ResourceAddress> Addresses);
