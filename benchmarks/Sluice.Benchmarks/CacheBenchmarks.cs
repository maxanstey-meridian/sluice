using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Benchmarks;

/// <summary>
/// What Sluice adds to a cache call, by how many dependencies the value read: a hit and a miss through Sluice, beside
/// FusionCache on its own, untagged and with as many tags as Sluice records. Memory only, so it measures CPU and
/// allocation, not Redis round trips.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CacheBenchmarks
{
    private static readonly Source<int> _rows = new("row");
    private static readonly Query<int, int> _query = new("value");

    private ServiceProvider _services = null!;
    private SluiceCache _sluice = null!;
    private FusionCache _fusion = null!;
    private Dependency[] _dependencies = [];
    private string[] _tags = [];

    [Params(1, 10, 100, 1000)]
    public int Dependencies { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _services = new ServiceCollection().AddLogging().AddSluice().BuildServiceProvider();
        _sluice = _services.GetRequiredService<SluiceCache>();
        _fusion = new FusionCache(new FusionCacheOptions());
        _dependencies = [.. Enumerable.Range(0, Dependencies).Select(_rows.For)];

        // Sluice's tags for these: one per row, and one for the source.
        _tags = [.. Enumerable.Range(0, Dependencies).Select(i => $"row:{i}"), "row:"];

        await HitSluice();
        await HitFusionCache();
        await HitFusionCacheTagged();
    }

    [GlobalCleanup]
    public async Task Teardown()
    {
        await _services.DisposeAsync();
        _fusion.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Hit")]
    public ValueTask<int> HitFusionCache() =>
        _fusion.GetOrSetAsync("value:0", _ => Task.FromResult(1));

    [Benchmark]
    [BenchmarkCategory("Hit")]
    public ValueTask<int> HitFusionCacheTagged() =>
        _fusion.GetOrSetAsync("tagged:0", _ => Task.FromResult(1), tags: _tags);

    [Benchmark]
    [BenchmarkCategory("Hit")]
    public ValueTask<int> HitSluice() =>
        _sluice.GetOrSetAsync(
            _query,
            0,
            (read, _) =>
            {
                read.From(_dependencies);
                return Task.FromResult(1);
            }
        );

    // Each miss benchmark clears its cache, then misses on the same keys again, so the cache doesn't grow from one
    // call to the next. The clear is shared by Misses calls, and FusionCache's clear and Sluice's cost the same.
    private const int Misses = 1000;

    [Benchmark(Baseline = true, OperationsPerInvoke = Misses)]
    [BenchmarkCategory("Miss")]
    public async Task MissFusionCache()
    {
        await _fusion.ClearAsync();
        for (var key = 0; key < Misses; key++)
        {
            await _fusion.GetOrSetAsync($"value:{key}", _ => Task.FromResult(1));
        }
    }

    [Benchmark(OperationsPerInvoke = Misses)]
    [BenchmarkCategory("Miss")]
    public async Task MissFusionCacheTagged()
    {
        await _fusion.ClearAsync();
        for (var key = 0; key < Misses; key++)
        {
            await _fusion.GetOrSetAsync($"value:{key}", _ => Task.FromResult(1), tags: _tags);
        }
    }

    [Benchmark(OperationsPerInvoke = Misses)]
    [BenchmarkCategory("Miss")]
    public async Task MissSluice()
    {
        await _sluice.ClearAsync();
        for (var key = 0; key < Misses; key++)
        {
            await _sluice.GetOrSetAsync(
                _query,
                key,
                (read, _) =>
                {
                    read.From(_dependencies);
                    return Task.FromResult(1);
                }
            );
        }
    }
}
