using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice;

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
    public static IServiceCollection AddSluice(
        this IServiceCollection services,
        Action<IFusionCacheBuilder>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

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
            return new SluiceCache(cache, tagOptions!);
        });

        return services;
    }
}
