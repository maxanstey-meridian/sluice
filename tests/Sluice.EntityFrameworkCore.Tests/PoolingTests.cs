using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// Pooled contexts and context factories, whose options are built once, and pooled instances, which serve one lease
/// after another.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PoolingTests(PostgresFixture postgres)
{
    public enum Registration
    {
        DbContextPool,
        PooledDbContextFactory,
        DbContextFactory,
    }

    [Theory]
    [InlineData(Registration.DbContextPool)]
    [InlineData(Registration.PooledDbContextFactory)]
    [InlineData(Registration.DbContextFactory)]
    public async Task Each_lease_evicts_what_it_commits_and_nothing_an_earlier_lease_left_uncommitted(
        Registration registration
    )
    {
        void Configure(IServiceProvider sp, DbContextOptionsBuilder options) =>
            options.UseNpgsql(postgres.ConnectionString).UseSluice(sp);
        await using var node = new SluiceNode(services: services =>
        {
            switch (registration)
            {
                // Computes get their context from a factory, which EF configures alongside the pool.
                case Registration.DbContextPool:
                    services.AddDbContextPool<ShopDb>(Configure, poolSize: 1);
                    services.AddDbContextFactory<ShopDb>(Configure);
                    break;
                case Registration.PooledDbContextFactory:
                    services.AddPooledDbContextFactory<ShopDb>(Configure, poolSize: 1);
                    break;
                case Registration.DbContextFactory:
                    services.AddDbContextFactory<ShopDb>(Configure);
                    break;
            }
        });
        var provider = node.Services;
        var cache = node.Cache;
        await using var reader = postgres.CreateDb();
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(cache, ada);
        var bobRead = await CachedRead.Customer(cache, bob);

        // A leased context, and what returns it to the pool.
        (ShopDb Db, IAsyncDisposable Return) Lease()
        {
            if (registration is not Registration.DbContextPool)
            {
                var created = provider
                    .GetRequiredService<IDbContextFactory<ShopDb>>()
                    .CreateDbContext();
                return (created, created);
            }

            var scope = provider.CreateAsyncScope();
            return (scope.ServiceProvider.GetRequiredService<ShopDb>(), scope);
        }

        var (db, lease) = Lease();
        var first = db.ContextId;
        await using (lease)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Rename(ada, "Ada Lovelace");
        }

        (db, lease) = Lease();
        await using (lease)
        {
            if (registration is not Registration.DbContextFactory)
            {
                db.ContextId.InstanceId.Should()
                    .Be(first.InstanceId, "the pool hands out the same instance");
                db.ContextId.Lease.Should().NotBe(first.Lease);
            }

            await db.Rename(bob, "Bob Babbage");
        }

        (await bobRead.WasEvicted()).Should().BeTrue();
        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the first lease's transaction never committed");
        (await reader.NameOf(ada)).Should().Be("Ada");
    }
}
