using System.Collections.Concurrent;
using System.Text;

namespace Sluice;

public sealed class InMemoryGraphStore : IGraphStore, IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ConcurrentDictionary<ResourceAddress, HashSet<string>> _reverseIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<ResourceAddress>> _forwardIndex = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cachedAt = new();

    public Task ClearEntryEdges(string entryKey, CancellationToken ct)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_forwardIndex.TryGetValue(entryKey, out var oldEdges))
            {
                _cachedAt.TryRemove(entryKey, out _);
                foreach (var address in oldEdges)
                {
                    if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                    {
                        continue;
                    }

                    entryKeys.Remove(entryKey);
                    if (entryKeys.Count == 0)
                    {
                        _reverseIndex.TryRemove(address, out _);
                    }
                }
                _forwardIndex.TryRemove(entryKey, out _);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    public Task RecordEntry(
        string entryKey,
        IReadOnlyList<ResourceAddress> addresses,
        DateTimeOffset cachedAt,
        CancellationToken ct
    )
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var address in addresses)
            {
                _reverseIndex.GetOrAdd(address, _ => new HashSet<string>()).Add(entryKey);
            }
            _forwardIndex[entryKey] = [.. addresses];
            _cachedAt[entryKey] = cachedAt;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> FindAffectedEntries(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    )
    {
        HashSet<string> affectedEntryKeys;

        _lock.EnterWriteLock();
        try
        {
            affectedEntryKeys = [];

            foreach (var address in changedAddresses)
            {
                if (address.Key == "*")
                {
                    foreach (var storedAddress in _reverseIndex.Keys.ToArray())
                    {
                        if (
                            storedAddress.Kind != address.Kind
                            || storedAddress.Name != address.Name
                        )
                        {
                            continue;
                        }

                        if (!_reverseIndex.TryGetValue(storedAddress, out var entryKeys))
                        {
                            continue;
                        }

                        foreach (var entryKey in entryKeys)
                        {
                            affectedEntryKeys.Add(entryKey);
                        }
                    }
                }
                else
                {
                    if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                    {
                        continue;
                    }

                    foreach (var entryKey in entryKeys)
                    {
                        affectedEntryKeys.Add(entryKey);
                    }
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.FromResult<IReadOnlyList<string>>([.. affectedEntryKeys]);
    }

    public Task FlushAsync(CancellationToken ct)
    {
        _lock.EnterWriteLock();
        try
        {
            _reverseIndex.Clear();
            _forwardIndex.Clear();
            _cachedAt.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    public Task<string> DumpGraphAsync(CancellationToken ct)
    {
        _lock.EnterReadLock();
        try
        {
            var sb = new StringBuilder();

            sb.AppendLine("OPERATIONS:");
            foreach (var (entryKey, reads) in _forwardIndex)
            {
                sb.AppendLine($"  {entryKey}");
                sb.AppendLine("    reads:");
                foreach (var addr in reads)
                {
                    sb.AppendLine($"      {addr}");
                }
                if (_cachedAt.TryGetValue(entryKey, out var ts))
                {
                    sb.AppendLine($"    cached: {ts:O}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("RESOURCE ADDRESSES:");
            foreach (var (address, entryKeys) in _reverseIndex)
            {
                sb.AppendLine($"  {address}");
                sb.AppendLine("    invalidates:");
                foreach (var ek in entryKeys)
                {
                    sb.AppendLine($"      {ek}");
                }
                sb.AppendLine();
            }

            return Task.FromResult(sb.ToString());
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose() => _lock.Dispose();
}
