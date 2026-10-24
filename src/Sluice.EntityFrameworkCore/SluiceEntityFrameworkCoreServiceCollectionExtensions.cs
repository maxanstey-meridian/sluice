using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sluice.EntityFrameworkCore;
using Sluice.Infrastructure;

namespace Sluice;

/// <summary>Registers Sluice's EF Core package in a service collection.</summary>
public static class SluiceEntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Starts the checks that a compute queries no context without <c>UseSluice</c> and caches no value holding a lazy
    /// loader or a context. <c>UseSluice</c> throws if this wasn't called. Call it where you call <c>AddSluice</c>.
    /// Calling it again does nothing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// Both start here, before any compute runs: a compute that ran before them could cache what a context without
    /// <c>UseSluice</c> read, or an entity that loads rows later, and nothing would evict it. The first listens to EF's
    /// diagnostic events, for every context in the process.
    /// </remarks>
    public static IServiceCollection AddSluiceEntityFrameworkCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ForeignContexts.Subscribe();
        StoreChecks.Add(services, CachedValues.Check);
        services.TryAddSingleton<Registration>();
        return services;
    }

    /// <summary>What <c>UseSluice</c> looks for to know <see cref="AddSluiceEntityFrameworkCore"/> was called.</summary>
    internal sealed class Registration;
}
