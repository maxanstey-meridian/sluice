using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Gives a <c>UseSluice</c> context's internal services <see cref="PoolReset"/>, which EF resets with the context, and
/// <see cref="ProviderNameConvention"/>, which names the provider on its model. The same for every cache, so it adds one
/// internal service provider, not one per cache.
/// </summary>
internal sealed class SluiceOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddScoped<IResettableService, PoolReset>();
        new EntityFrameworkServicesBuilder(services).TryAdd<
            IConventionSetPlugin,
            ProviderNameConvention
        >();
    }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using Sluice ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Sluice"] = "1";
    }
}

/// <summary>
/// Hands the context's connection to its cache's interceptor when EF resets the context for the pool's next lease,
/// which raises no event for a transaction passed to <c>UseTransaction</c>, nor for closing a connection EF owns.
/// EF resets its services in the order they were registered, so this may run before or after it resets the
/// connection, and the interceptor publishes only what belongs to transactions that have ended.
/// </summary>
internal sealed class PoolReset(
    IDbContextOptions options,
    IDbContextTransactionManager transactions
) : IResettableService
{
    public void ResetState()
    {
        if (Interceptor() is { } interceptor && transactions is IRelationalConnection connection)
        {
            interceptor.Reset(connection);
        }
    }

    public Task ResetStateAsync(CancellationToken cancellationToken = default) =>
        Interceptor() is { } interceptor && transactions is IRelationalConnection connection
            ? interceptor.ResetAsync(connection)
            : Task.CompletedTask;

    // UseSluice adds one. A provider that isn't relational has no connection, and nothing Sluice holds.
    private SluiceInterceptor? Interceptor() =>
        options
            .FindExtension<CoreOptionsExtension>()
            ?.Interceptors?.OfType<SluiceInterceptor>()
            .FirstOrDefault();
}
