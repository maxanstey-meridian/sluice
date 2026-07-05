using System.Collections.Concurrent;

namespace Sluice;

public sealed class SluiceKernel(ICacheStore cacheStore, IGraphStore? graphStore = null)
    : ISluice,
        IDisposable
{
    private readonly OperationRegistry _registry = new(cacheStore, graphStore);
    private readonly ConcurrentDictionary<object, byte> _registeredQueries = new();

    public async Task<TValue> Get<TKey, TValue>(
        Query<TKey, TValue> query,
        TKey key,
        CancellationToken ct
    )
    {
        if (_registeredQueries.TryAdd(query, 0))
        {
            _registry.Register(query.Operation);
        }
        return await _registry.ExecuteAsync(query.Operation, key, ct);
    }

    public async Task Apply(
        Func<CancellationToken, Task> work,
        Action<ChangeBuilder> changes,
        CancellationToken ct
    )
    {
        var builder = new ChangeBuilder();
        changes(builder);
        var effect = builder.ToWriteEffect();
        await _registry.ApplyAsync(async ctx => await ctx.Apply(() => work(ct), effect), ct);
    }

    public async Task Apply(
        Func<CancellationToken, Task> work,
        WriteEffect effect,
        CancellationToken ct
    )
    {
        await _registry.ApplyAsync(async ctx => await ctx.Apply(() => work(ct), effect), ct);
    }

    public async Task<T> Apply<T>(
        Func<CancellationToken, Task<T>> work,
        Action<ChangeBuilder<T>> changes,
        CancellationToken ct
    )
    {
        var builder = new ChangeBuilder<T>();
        changes(builder);
        var effect = builder.ToWriteEffect();
        return await _registry.ApplyAsync<T>(
            async ctx => await ctx.Apply(() => work(ct), effect),
            ct
        );
    }

    public async Task<T> Apply<T>(
        Func<CancellationToken, Task<T>> work,
        WriteEffect<T> effect,
        CancellationToken ct
    )
    {
        return await _registry.ApplyAsync<T>(
            async ctx => await ctx.Apply(() => work(ct), effect),
            ct
        );
    }

    public Task FlushAllAsync(CancellationToken ct) => _registry.FlushAllAsync(ct);

    public Task<string> DumpGraphAsync(CancellationToken ct) => _registry.DumpGraphAsync(ct);

    public SystemManifest Describe() => _registry.Describe();

    public void Dispose() => _registry.Dispose();
}
