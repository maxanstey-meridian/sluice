using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Benchmarks;

public sealed class Item
{
    public int Id { get; set; }
    public int Value { get; set; }
}

public sealed class ItemDb(DbContextOptions<ItemDb> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
}

/// <summary>
/// What <c>UseSluice</c> adds to EF: a save of one changed row, and a cache miss that reads one row on the compute's
/// own pooled context, through a read helper and as a plain query, each beside the same without Sluice. On in-memory
/// SQLite, so the database is about as fast as it gets and Sluice's share is as large as it gets.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class EfBenchmarks
{
    private static readonly Query<int, int> _query = new("item-value");

    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private SluiceCache _sluice = null!;
    private FusionCache _fusion = null!;
    private ItemDb _plain = null!;
    private ItemDb _withSluice = null!;
    private Item _plainItem = null!;
    private Item _sluiceItem = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddLogging()
            .AddSluice()
            .AddPooledDbContextFactory<ItemDb>(
                (services, options) => options.UseSqlite(_connection).UseSluice(services)
            )
            .BuildServiceProvider();
        _sluice = _services.GetRequiredService<SluiceCache>();
        _fusion = new FusionCache(new FusionCacheOptions());

        _plain = new ItemDb(new DbContextOptionsBuilder<ItemDb>().UseSqlite(_connection).Options);
        _withSluice = new ItemDb(
            new DbContextOptionsBuilder<ItemDb>()
                .UseSqlite(_connection)
                .UseSluice(_services)
                .Options
        );
        await _plain.Database.EnsureCreatedAsync();
        _plain.Items.AddRange(new Item { Id = 1 }, new Item { Id = 2 });
        await _plain.SaveChangesAsync();
        _plainItem = await _plain.Items.SingleAsync(item => item.Id == 1);
        _sluiceItem = await _withSluice.Items.SingleAsync(item => item.Id == 2);
    }

    [GlobalCleanup]
    public async Task Teardown()
    {
        await _plain.DisposeAsync();
        await _withSluice.DisposeAsync();
        await _services.DisposeAsync();
        _fusion.Dispose();
        await _connection.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Save")]
    public Task<int> SaveWithoutSluice()
    {
        _plainItem.Value++;
        return _plain.SaveChangesAsync();
    }

    [Benchmark]
    [BenchmarkCategory("Save")]
    public Task<int> SaveWithSluice()
    {
        _sluiceItem.Value++;
        return _withSluice.SaveChangesAsync();
    }

    // As in CacheBenchmarks: clear, then miss on the same keys again.
    private const int Misses = 100;

    [Benchmark(Baseline = true, OperationsPerInvoke = Misses)]
    [BenchmarkCategory("Miss")]
    public async Task MissFusionCacheWithPlainQuery()
    {
        await _fusion.ClearAsync();
        for (var key = 0; key < Misses; key++)
        {
            await _fusion.GetOrSetAsync(
                $"item-value:{key}",
                ct =>
                    _plain
                        .Items.AsNoTracking()
                        .Where(item => item.Id == 1)
                        .Select(item => item.Value)
                        .SingleAsync(ct)
            );
        }
    }

    [Benchmark(OperationsPerInvoke = Misses)]
    [BenchmarkCategory("Miss")]
    public async Task MissSluiceWithReadHelper()
    {
        await _sluice.ClearAsync();
        for (var key = 0; key < Misses; key++)
        {
            await _sluice.GetOrSetAsync(
                _query,
                key,
                (read, ct) =>
                    read.Entity(read.Db<ItemDb>().Items, 1)
                        .Select(item => item.Value)
                        .SingleAsync(ct)
            );
        }
    }

    [Benchmark(OperationsPerInvoke = Misses)]
    [BenchmarkCategory("Miss")]
    public async Task MissSluiceWithPlainQuery()
    {
        await _sluice.ClearAsync();
        for (var key = 0; key < Misses; key++)
        {
            await _sluice.GetOrSetAsync(
                _query,
                key,
                (read, ct) =>
                    read.Db<ItemDb>()
                        .Items.Where(item => item.Id == 1)
                        .Select(item => item.Value)
                        .SingleAsync(ct)
            );
        }
    }
}
