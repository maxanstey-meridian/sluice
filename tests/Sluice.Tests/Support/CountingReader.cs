using System.Collections.Concurrent;

namespace Sluice.Tests.Support;

/// <summary>Reads one query and counts how often its compute ran for each key.</summary>
public sealed class CountingReader(SluiceCache cache, Query<string, string> query)
{
    private readonly ConcurrentDictionary<string, int> _runs = new();

    public int Runs(string key) => _runs.GetValueOrDefault(key);

    /// <summary>Gets the value for <paramref name="key"/>; on a miss the compute records <paramref name="dependencies"/>.</summary>
    public async Task<string> GetAsync(string key, params Dependency[] dependencies) =>
        await cache.GetOrSetAsync(
            query,
            key,
            (read, _) =>
            {
                var run = _runs.AddOrUpdate(key, 1, (_, runs) => runs + 1);
                read.From(dependencies);
                return Task.FromResult($"{key}#{run}");
            }
        );
}
