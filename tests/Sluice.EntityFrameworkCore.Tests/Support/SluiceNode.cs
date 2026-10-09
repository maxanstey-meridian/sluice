using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// One application instance: a service provider with <c>AddSluice</c> and logging, as an app would build it.
/// </summary>
public sealed class SluiceNode : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public SluiceNode(Action<IFusionCacheBuilder>? configure = null)
    {
        _provider = new ServiceCollection()
            .AddFakeLogging()
            .AddSluice(configure)
            .BuildServiceProvider();
        Cache = _provider.GetRequiredService<SluiceCache>();
    }

    public SluiceCache Cache { get; }

    /// <summary>What <c>UseSluice</c> takes.</summary>
    public IServiceProvider Services => _provider;

    public FakeLogCollector Logs => _provider.GetFakeLogCollector();

    /// <summary>Sluice's underlying FusionCache, for arranging states the public API can't (an expired entry).</summary>
    public IFusionCache Fusion =>
        _provider.GetRequiredService<IFusionCacheProvider>().GetCache("sluice");

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
