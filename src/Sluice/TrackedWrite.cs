namespace Sluice;

public sealed class TrackedWrite<TKey>(
    ISluice sluice,
    params Func<TKey, ResourceAddress>[] addresses
)
{
    public WriteEffect For(TKey key) => new(addresses.Select(f => f(key)).ToArray());

    public Task Write(TKey key, Func<CancellationToken, Task> work, CancellationToken ct) =>
        sluice.Apply(work, For(key), ct);
}
