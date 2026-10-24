using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Sluice.Infrastructure;

/// <summary>
/// How a package refuses a computed value before <see cref="SluiceCache"/> stores it, such as the EF Core package's
/// check for an entity that would load related rows after the compute. For Sluice's own packages; it isn't a
/// supported API, and may change in any release.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class StoreChecks
{
    /// <summary>
    /// Registers a check that every value the <see cref="SluiceCache"/> built from <paramref name="services"/> is
    /// about to store goes through: once the compute and its before-store steps have returned, on the compute's flow,
    /// and only when the value will be stored (not when the compute threw, recorded a violation or no dependencies,
    /// called <see cref="SluiceReads.DoNotCache"/>, or ran while the flow had uncommitted writes). A null value isn't
    /// checked. The first check that returns an exception makes it the call's violation: nothing is stored and the
    /// caller gets it. A check already registered isn't registered again. Registered with the services, it's in place
    /// before the cache's first compute.
    /// </summary>
    /// <param name="services">The service collection <c>AddSluice</c> registers the cache in.</param>
    /// <param name="check">
    /// Returns why the value mustn't be stored, or null to let it through. It may be called from concurrent computes.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection Add(
        IServiceCollection services,
        Func<object, Exception?> check
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(check);
        if (
            !services.Any(service =>
                service.ImplementationInstance is Registered registered
                && registered.Check.Equals(check)
            )
        )
        {
            services.AddSingleton(new Registered(check));
        }

        return services;
    }

    internal sealed record Registered(Func<object, Exception?> Check);
}
