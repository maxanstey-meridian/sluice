namespace Sluice;

public interface ISluice
{
    public Task<TValue> Get<TKey, TValue>(
        Query<TKey, TValue> query,
        TKey key,
        CancellationToken ct
    );

    public Task Apply(
        Func<CancellationToken, Task> work,
        Action<ChangeBuilder> changes,
        CancellationToken ct
    );

    public Task Apply(
        Func<CancellationToken, Task> work,
        WriteEffect effect,
        CancellationToken ct
    );

    public Task<T> Apply<T>(
        Func<CancellationToken, Task<T>> work,
        Action<ChangeBuilder<T>> changes,
        CancellationToken ct
    );

    public Task<T> Apply<T>(
        Func<CancellationToken, Task<T>> work,
        WriteEffect<T> effect,
        CancellationToken ct
    );

    public Task FlushAllAsync(CancellationToken ct);
}
