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

    /// <param name="configure">The app's FusionCache configuration, passed to <c>AddSluice</c>.</param>
    /// <param name="services">The app's other registrations, such as a pooled context.</param>
    public SluiceNode(
        Action<IFusionCacheBuilder>? configure = null,
        Action<IServiceCollection>? services = null
    )
    {
        var collection = new ServiceCollection().AddFakeLogging().AddSluice(configure);
        services?.Invoke(collection);
        _provider = collection.BuildServiceProvider();
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
