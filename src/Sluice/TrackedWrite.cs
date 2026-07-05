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

public sealed class TrackedWrite<TKey, TResult>(
    ISluice sluice,
    Func<TKey, ResourceAddress>[] staticAddresses,
    params Func<TResult, ResourceAddress>[] resultAddresses
)
{
    public Task<TResult> Write(
        TKey key,
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken ct
    )
    {
        var effect = new WriteEffect<TResult>(
            staticAddresses.Select(f => f(key)).ToArray()
        );
        foreach (var resolver in resultAddresses)
        {
            effect.ChangesResult(resolver);
        }
        return sluice.Apply(work, effect, ct);
    }
}
