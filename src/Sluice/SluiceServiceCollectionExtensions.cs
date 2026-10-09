using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice;

/// <summary>Registers Sluice in a service collection.</summary>
public static class SluiceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SluiceCache"/> over a named FusionCache (<c>"sluice"</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// FusionCache's own builder, for the default duration, L2, backplane, serializer and so on.
    /// Sluice then enforces the settings it depends on (key prefix, remove-by-tag behaviour, tagging on),
    /// overriding any conflicting value set here.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">Sluice is already registered.</exception>
    /// <remarks>
    /// The entry lifetimes configured here are checked against the tag-data lifetimes when <see cref="SluiceCache"/>
    /// is first resolved, which throws <see cref="InvalidOperationException"/> if an entry could outlive the tag data
    /// that invalidates it.
    /// </remarks>
    public static IServiceCollection AddSluice(
        this IServiceCollection services,
        Action<IFusionCacheBuilder>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(service => service.ServiceType == typeof(SluiceCache)))
        {
            throw new InvalidOperationException(
                "AddSluice was called more than once. Call it once, and pass all of Sluice's FusionCache "
                    + "configuration to that call."
            );
        }

        var builder = services.AddFusionCache(SluiceCache.CacheName);
        configure?.Invoke(builder);

        // FusionCache copies its options and doesn't expose TagsDefaultEntryOptions afterwards,
        // so capture them here for the lifetime check. These actions run after the user's.
        FusionCacheEntryOptions? tagOptions = null;
        builder
            .WithCacheKeyPrefixByCacheName()
            .WithOptions(options =>
            {
                options.DisableTagging = false;
                options.RemoveByTagBehavior = RemoveByTagBehavior.Remove;
                tagOptions = options.TagsDefaultEntryOptions;
            });

        services.AddSingleton(provider =>
        {
            var cache = provider
                .GetRequiredService<IFusionCacheProvider>()
                .GetCache(SluiceCache.CacheName);
            return new SluiceCache(
                cache,
                provider,
                tagOptions
                    ?? throw new UnreachableException(
                        "FusionCache built the Sluice cache without running Sluice's options action."
                    )
            );
        });

        return services;
    }
}
