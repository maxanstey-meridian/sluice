using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.Memory;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.Tests.Support;

/// <summary>One application instance: a service provider with <c>AddSluice</c>, as an app would build it.</summary>
public sealed class SluiceNode : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public SluiceNode(Action<IFusionCacheBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSluice(configure);
        _provider = services.BuildServiceProvider();
        try
        {
            Cache = _provider.GetRequiredService<SluiceCache>();
        }
        catch
        {
            _provider.Dispose();
            throw;
        }
    }

    public SluiceCache Cache { get; }

    /// <summary>Sluice's underlying FusionCache, for arranging states the public API can't (an expired entry).</summary>
    public IFusionCache Fusion =>
        _provider.GetRequiredService<IFusionCacheProvider>().GetCache("sluice");

    /// <summary>A node sharing <paramref name="l2"/> and the in-memory backplane <paramref name="bus"/> with its peers.</summary>
    public static SluiceNode Clustered(
        IDistributedCache l2,
        string bus,
        Action<IFusionCacheBuilder>? configure = null
    ) =>
        new(cache =>
        {
            // FusionCache then awaits each backplane publish (tag data already does by default), and
            // MemoryBackplane runs every peer's handler inline, so a peer has processed a notification
            // by the time the call that sent it returns.
            cache
                .WithOptions(options => options.WaitForInitialBackplaneSubscribe = true)
                .WithDefaultEntryOptions(options =>
                    options.AllowBackgroundBackplaneOperations = false
                )
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(l2)
                .WithBackplane(
                    new MemoryBackplane(new MemoryBackplaneOptions { ConnectionId = bus })
                );
            configure?.Invoke(cache);
        });

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
