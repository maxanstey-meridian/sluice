using System.Collections.Concurrent;

namespace Sluice;

public sealed class OperationRegistry(
    ICacheStore cacheStore,
    IGraphStore? graphStore = null,
    TimeProvider? clock = null,
    IStampedeCoordinator? stampedeCoordinator = null,
    StampedeOptions? stampedeOptions = null
) : IDisposable
{
    private readonly IGraphStore _graphStore = graphStore ?? new InMemoryGraphStore();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IStampedeCoordinator _stampede =
        stampedeCoordinator ?? new InMemoryStampedeCoordinator();
    private readonly StampedeOptions _stampedeOptions = stampedeOptions ?? new StampedeOptions();
    private readonly ConcurrentBag<IOperation> _operationMetadata = [];
    private long _writeEpoch;
    private readonly ConcurrentQueue<InvalidationRecord> _recentInvalidations = new();
    private const int MaxRecentInvalidations = 256;

    private sealed record InvalidationRecord(long Epoch, IReadOnlyList<ResourceAddress> Addresses);

    public OperationRegistry Register<TKey, TValue>(CachedOperation<TKey, TValue> operation)
    {
        _operationMetadata.Add(operation);
        return this;
    }

    public async Task<TValue> ExecuteAsync<TKey, TValue>(
        CachedOperation<TKey, TValue> operation,
        TKey key,
        CancellationToken ct
    )
    {
        var entryKey = operation.BuildEntryKey(key);

        var cached = await cacheStore.GetAsync<TValue>(entryKey, ct);
        if (cached is not null)
        {
            if (cached.ExpiresAt is { } expiresAt && expiresAt <= _clock.GetUtcNow())
            {
                await cacheStore.RemoveAsync(entryKey, ct);
            }
            else
            {
                return cached.Value;
            }
        }

        var lease = await _stampede.TryAcquireAsync(entryKey, _stampedeOptions.LeaseTtl, ct);

        if (lease is null)
        {
            var polled = await PollForEntry<TValue>(entryKey, ct);
            if (polled is not null)
            {
                return polled.Value;
            }

            lease = await _stampede.TryAcquireAsync(entryKey, _stampedeOptions.LeaseTtl, ct);
            if (lease is null)
            {
                return await ComputeAndStoreAsync(operation, entryKey, key, ct);
            }
        }

        try
        {
            var rechecked = await cacheStore.GetAsync<TValue>(entryKey, ct);
            if (rechecked is null)
            {
                return await ComputeAndStoreAsync(operation, entryKey, key, ct);
            }

            if (rechecked.ExpiresAt is { } expiresAt && expiresAt <= _clock.GetUtcNow())
            {
                await cacheStore.RemoveAsync(entryKey, ct);
            }
            else
            {
                return rechecked.Value;
            }

            return await ComputeAndStoreAsync(operation, entryKey, key, ct);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    private async Task<CacheEntry<TValue>?> PollForEntry<TValue>(
        string entryKey,
        CancellationToken ct
    )
    {
        var deadline = _clock.GetUtcNow() + _stampedeOptions.WaitTimeout;
        var delay = TimeSpan.FromMilliseconds(1);
        while (_clock.GetUtcNow() < deadline)
        {
            var entry = await cacheStore.GetAsync<TValue>(entryKey, ct);
            if (entry is not null)
            {
                if (entry.ExpiresAt is { } expiresAt && expiresAt <= _clock.GetUtcNow())
                {
                    await cacheStore.RemoveAsync(entryKey, ct);
                }
                else
                {
                    return entry;
                }
            }

            await Task.Delay(delay, _clock, ct);
            var doubled = TimeSpan.FromTicks(delay.Ticks * 2);
            delay = doubled < _stampedeOptions.MaxBackoff ? doubled : _stampedeOptions.MaxBackoff;
        }
        return null;
    }

    private async Task<TValue> ComputeAndStoreAsync<TKey, TValue>(
        CachedOperation<TKey, TValue> operation,
        string entryKey,
        TKey key,
        CancellationToken ct
    )
    {
        var epochBefore = Interlocked.Read(ref _writeEpoch);

        await _graphStore.ClearEntryEdges(entryKey, ct);

        var ctx = new OperationContext(_clock, ct);
        var value = await operation.RunCompute(key, ctx);

        var now = _clock.GetUtcNow();
        DateTimeOffset? entryExpiresAt = operation.Ttl is { } ttl ? now + ttl : null;
        var entry = new CacheEntry<TValue>(
            value,
            [.. ctx.ObservedReads],
            now,
            entryExpiresAt,
            epochBefore
        );

        await cacheStore.SetAsync(entryKey, entry, ct);

        await _graphStore.RecordEntry(entryKey, ctx.ObservedReads, now, ct);

        var snapshot = _recentInvalidations.ToArray();
        var epochAfter = Interlocked.Read(ref _writeEpoch);

        if (epochAfter <= epochBefore)
        {
            return value;
        }

        var shouldInvalidate = false;
        if (epochAfter - epochBefore >= MaxRecentInvalidations)
        {
            shouldInvalidate = true;
        }
        else
        {
            foreach (var record in snapshot)
            {
                if (record.Epoch <= epochBefore)
                {
                    continue;
                }

                if (!record.Addresses.Any(changed => Overlaps(changed, ctx.ObservedReads)))
                {
                    continue;
                }

                shouldInvalidate = true;
                break;
            }
        }

        if (!shouldInvalidate)
        {
            return value;
        }

        await cacheStore.RemoveAsync(entryKey, ct);
        await _graphStore.ClearEntryEdges(entryKey, ct);

        return value;
    }

    public async Task ApplyAsync(Func<ChangeContext, Task> write, CancellationToken ct)
    {
        var ctx = new ChangeContext(ct);
        await write(ctx);
        await InvalidateAsync(ctx.ChangedAddresses, ct);
    }

    public async Task<T> ApplyAsync<T>(Func<ChangeContext, Task<T>> write, CancellationToken ct)
    {
        var ctx = new ChangeContext(ct);
        var result = await write(ctx);
        await InvalidateAsync(ctx.ChangedAddresses, ct);
        return result;
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        await cacheStore.ClearAsync(ct);
        await _graphStore.FlushAsync(ct);
        await _stampede.ClearAsync(ct);
    }

    internal async Task InvalidateAsync(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    )
    {
        var epoch = Interlocked.Increment(ref _writeEpoch);
        _recentInvalidations.Enqueue(new InvalidationRecord(epoch, changedAddresses));
        while (_recentInvalidations.Count > MaxRecentInvalidations)
        {
            _recentInvalidations.TryDequeue(out _);
        }

        var affectedEntryKeys = await _graphStore.FindAffectedEntries(changedAddresses, ct);

        foreach (var entryKey in affectedEntryKeys)
        {
            await cacheStore.RemoveAsync(entryKey, ct);
            await _graphStore.ClearEntryEdges(entryKey, ct);
        }
    }

    private static bool Overlaps(
        ResourceAddress changedAddress,
        IReadOnlyList<ResourceAddress> observedReads
    )
    {
        foreach (var observed in observedReads)
        {
            if (changedAddress == observed)
            {
                return true;
            }
            if (
                changedAddress.Key == "*"
                && changedAddress.Kind == observed.Kind
                && changedAddress.Name == observed.Name
            )
            {
                return true;
            }
        }
        return false;
    }

    public Task<string> DumpGraphAsync(CancellationToken ct) => _graphStore.DumpGraphAsync(ct);

    public SystemManifest Describe()
    {
        var operations = _operationMetadata
            .Select(op => new OperationInfo(
                op.Name,
                op.KeyType.Name,
                op.ValueType.Name,
                op.GetType().Name
            ))
            .OrderBy(op => op.DefinedBy)
            .ToList();
        return new SystemManifest(operations);
    }

    public void Dispose()
    {
        if (_graphStore is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
