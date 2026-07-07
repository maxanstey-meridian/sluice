namespace Sluice;

public interface ISluice
{
    public Task<TValue> Get<TKey, TValue>(
        CachedQuery<TKey, TValue> query,
        TKey key,
        CancellationToken ct
    )
        where TKey : IResourceKey;

    public Task Apply(Func<CancellationToken, Task> work, WriteEffect effect, CancellationToken ct);

    public Task<T> Apply<T>(
        Func<CancellationToken, Task<T>> work,
        WriteEffect<T> effect,
        CancellationToken ct
    );

    public Task Invalidate(WriteEffect effect, CancellationToken ct);

    public Task FlushAllAsync(CancellationToken ct);
}
