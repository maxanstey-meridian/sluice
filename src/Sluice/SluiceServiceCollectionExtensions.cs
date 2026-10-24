using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
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
    /// Sluice then enforces the settings it depends on (key prefix, remove-by-tag behaviour, tagging on, and tag
    /// markers that a memory cache's size limit never evicts), overriding any conflicting value set here.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">Sluice is already registered.</exception>
    /// <remarks>
    /// The entry lifetimes configured here are checked against the tag-data lifetimes when <see cref="SluiceCache"/>
    /// is first resolved, which throws <see cref="InvalidOperationException"/> if an entry could outlive the tag data
    /// that invalidates it, or if the <c>InternalStrings</c> set here could mix tags with entries or dependencies with
    /// clears: a <c>TagCacheKeyPrefix</c> that doesn't start with <c>__fc</c>, or a clear tag with ':' in it. A
    /// <see cref="TimeProvider"/> registered here times the re-publishing of invalidations; otherwise it's
    /// <see cref="TimeProvider.System"/>.
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

        // FusionCache copies its options and doesn't expose TagsDefaultEntryOptions or InternalStrings afterwards,
        // so capture them here for the lifetime and tag prefix checks. These actions run after the user's.
        FusionCacheEntryOptions? tagOptions = null;
        FusionCacheInternalStrings? internalStrings = null;
        builder
            .WithCacheKeyPrefixByCacheName()
            .WithOptions(options =>
            {
                options.DisableTagging = false;
                options.RemoveByTagBehavior = RemoveByTagBehavior.Remove;

                // FusionCache writes every tag and clear marker with these. Under a memory SizeLimit, a marker that
                // was compacted away, or refused for want of room, reads as never invalidated, so an invalidated
                // value is served again: markers are never removed, and take no room (FusionCache's own defaults).
                options.TagsDefaultEntryOptions.Priority = CacheItemPriority.NeverRemove;
                options.TagsDefaultEntryOptions.Size = 0;
                tagOptions = options.TagsDefaultEntryOptions;
                internalStrings = options.InternalStrings;
            });

        services.AddSingleton(provider =>
        {
            var cache = provider
                .GetRequiredService<IFusionCacheProvider>()
                .GetCache(SluiceCache.CacheName);
            return tagOptions is null || internalStrings is null
                ? throw new UnreachableException(
                    "FusionCache built the Sluice cache without running Sluice's options action."
                )
                : new SluiceCache(
                    cache,
                    provider,
                    tagOptions,
                    internalStrings,
                    provider.GetService<TimeProvider>() ?? TimeProvider.System
                );
        });

        return services;
    }
}
