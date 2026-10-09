using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>Pooled contexts, whose options are built once and whose instances serve one lease after another.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PoolingTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pooled_context_evicts_what_each_lease_commits_and_nothing_an_earlier_lease_left_uncommitted(
        bool factory
    )
    {
        var services = new ServiceCollection().AddFakeLogging().AddSluice();
        if (factory)
        {
            services.AddPooledDbContextFactory<ShopDb>(
                (sp, options) => options.UseNpgsql(postgres.ConnectionString).UseSluice(sp),
                poolSize: 1
            );
        }
        else
        {
            services.AddDbContextPool<ShopDb>(
                (sp, options) => options.UseNpgsql(postgres.ConnectionString).UseSluice(sp),
                poolSize: 1
            );
        }

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<SluiceCache>();
        await using var reader = postgres.CreateDb();
        var ada = new Customer { Name = "Ada" };
        var bob = new Customer { Name = "Bob" };
        reader.Customers.AddRange(ada, bob);
        await reader.SaveChangesAsync();
        var adaRead = await CachedRead.Of(
            cache,
            (read, ct) => read.Entity(reader.Customers, ada.Id).SingleAsync(ct)
        );
        var bobRead = await CachedRead.Of(
            cache,
            (read, ct) => read.Entity(reader.Customers, bob.Id).SingleAsync(ct)
        );

        // A leased context, and what returns it to the pool.
        (ShopDb Db, IAsyncDisposable Return) Lease()
        {
            if (factory)
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
            (await db.Customers.SingleAsync(c => c.Id == ada.Id)).Name = "Ada Lovelace";
            await db.SaveChangesAsync();
        }

        (db, lease) = Lease();
        await using (lease)
        {
            db.ContextId.InstanceId.Should()
                .Be(first.InstanceId, "the pool hands out the same instance");
            db.ContextId.Lease.Should().NotBe(first.Lease);
            (await db.Customers.SingleAsync(c => c.Id == bob.Id)).Name = "Bob Babbage";
            await db.SaveChangesAsync();
        }

        (await bobRead.WasEvicted()).Should().BeTrue();
        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the first lease's transaction never committed");
        (await reader.Customers.AsNoTracking().SingleAsync(c => c.Id == ada.Id))
            .Name.Should()
            .Be("Ada");
    }
}
