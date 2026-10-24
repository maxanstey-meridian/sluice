using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>Registers Sluice's EF Core package in a service collection.</summary>
public static class SluiceEntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Starts the check that a compute queries no context without <c>UseSluice</c>, which <c>UseSluice</c> requires.
    /// Call it where you call <c>AddSluice</c>. Calling it again does nothing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// The check listens to EF's diagnostic events, for every context in the process, so it starts here, before any
    /// compute runs: a compute that ran before it started could cache what such a context read, and nothing would
    /// evict it.
    /// </remarks>
    public static IServiceCollection AddSluiceEntityFrameworkCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ForeignContexts.Subscribe();
        services.TryAddSingleton<Registration>();
        return services;
    }

    /// <summary>What <c>UseSluice</c> looks for to know <see cref="AddSluiceEntityFrameworkCore"/> was called.</summary>
    internal sealed class Registration;
}
