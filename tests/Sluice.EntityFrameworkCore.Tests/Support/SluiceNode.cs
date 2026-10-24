using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// One application instance: a service provider with <c>AddSluice</c>, logging, and <see cref="ShopDb"/> registered
/// through <c>AddDbContextFactory</c> with <c>UseSluice</c>, which also registers the scoped <see cref="ShopDb"/> that
/// <c>read.Db&lt;ShopDb&gt;()</c> takes, as an app would build it.
/// </summary>
public sealed class SluiceNode : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    /// <param name="configure">The app's FusionCache configuration, passed to <c>AddSluice</c>.</param>
    /// <param name="services">
    /// The app's other registrations, such as a pooled context. One that registers <see cref="ShopDb"/> or its
    /// factory replaces the default factory. Without a <see cref="TimeProvider"/>, the node's clock never moves, so an
    /// invalidation's re-publish waits for the node's disposal, whatever the test's timing.
    /// </param>
    /// <param name="validateScopes">Whether the provider refuses scoped services from the root, as in Development.</param>
    public SluiceNode(
        Action<IFusionCacheBuilder>? configure = null,
        Action<IServiceCollection>? services = null,
        bool validateScopes = false
    )
    {
        var collection = new ServiceCollection()
            .AddFakeLogging()
            .AddSluice(configure)
            .AddSluiceEntityFrameworkCore();
        services?.Invoke(collection);
        collection.TryAddSingleton<TimeProvider>(new FakeTimeProvider());
        if (
            !collection.Any(service =>
                service.ServiceType == typeof(IDbContextFactory<ShopDb>)
                || service.ServiceType == typeof(ShopDb)
            )
        )
        {
            collection.AddDbContextFactory<ShopDb>(
                (provider, options) =>
                    options.UseNpgsql(PostgresFixture.Running.ConnectionString).UseSluice(provider)
            );
        }

        _provider = collection.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = validateScopes }
        );
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
