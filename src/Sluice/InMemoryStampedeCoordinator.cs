using System.Collections.Concurrent;

namespace Sluice;

public sealed class InMemoryStampedeCoordinator : IStampedeCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public Task<IRefreshLease?> TryAcquireAsync(
        string entryKey,
        TimeSpan leaseTtl,
        CancellationToken ct
    )
    {
        var semaphore = _locks.GetOrAdd(entryKey, _ => new SemaphoreSlim(1, 1));
        if (semaphore.Wait(0))
        {
            return Task.FromResult<IRefreshLease?>(
                new Lease(entryKey, Guid.NewGuid().ToString("N"), semaphore)
            );
        }
        return Task.FromResult<IRefreshLease?>(null);
    }

    public Task ClearAsync(CancellationToken ct)
    {
        _locks.Clear();
        return Task.CompletedTask;
    }

    private sealed class Lease(string entryKey, string token, SemaphoreSlim semaphore)
        : IRefreshLease
    {
        private int _disposed;

        public string EntryKey { get; } = entryKey;
        public string Token { get; } = token;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
