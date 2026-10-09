using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Benchmarks;

/// <summary>
/// Invalidating one key, which removes two tags, beside FusionCache removing the same two itself. Memory only: with
/// Redis, each tag is also a write and a backplane message.
/// </summary>
[MemoryDiagnoser]
public class InvalidationBenchmarks
{
    private static readonly Source<int> _rows = new("row");

    private ServiceProvider _services = null!;
    private SluiceCache _sluice = null!;
    private FusionCache _fusion = null!;
    private int _key;

    // Each invalidation stores the time it happened for its tags, so a bounded set of keys keeps that from growing.
    private const int Keys = 1000;

    [GlobalSetup]
    public void Setup()
    {
        _services = new ServiceCollection().AddLogging().AddSluice().BuildServiceProvider();
        _sluice = _services.GetRequiredService<SluiceCache>();
        _fusion = new FusionCache(new FusionCacheOptions());
    }

    [GlobalCleanup]
    public async Task Teardown()
    {
        await _services.DisposeAsync();
        _fusion.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task FusionCacheRemoveByTag()
    {
        var key = _key++ % Keys;
        await _fusion.RemoveByTagAsync($"row:{key}");
        await _fusion.RemoveByTagAsync("row:*");
    }

    [Benchmark]
    public ValueTask SluiceInvalidate() => _sluice.InvalidateAsync(_rows.For(_key++ % Keys));
}
