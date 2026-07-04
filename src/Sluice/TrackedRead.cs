namespace Sluice;

public sealed class TrackedRead<TKey, TValue>(
    Func<TKey, ResourceAddress> address,
    Func<TKey, CancellationToken, Task<TValue>> read
)
{
    public ResourceAddress For(TKey key) => address(key);

    public Task<TValue> Get(TKey key, IReadScope scope) =>
        scope.Track(address(key), ct => read(key, ct));
}
