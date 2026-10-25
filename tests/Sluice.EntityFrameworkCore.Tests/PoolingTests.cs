using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    public async Task Each_lease_evicts_what_it_wrote_and_a_later_lease_s_save_publishes_nothing_an_earlier_lease_wrote(
        Registration registration
    )
    {
        void Configure(IServiceProvider sp, DbContextOptionsBuilder options) =>
            options.UseNpgsql(postgres.ConnectionString).UseSluice(sp);
        await using var node = new SluiceNode(services: services =>
        {
            switch (registration)
            {
                // A factory beside the pool, as an app might register both; computes take the pool's scoped context.
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

        // Ending the lease disposes the abandoned transaction EF began, and what a transaction still has pending when
        // it's disposed is published, since Sluice can't tell it from one committed outside EF, and over-invalidating
        // is safe.
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the first lease's transaction was disposed with its write pending");
        (await reader.NameOf(ada)).Should().Be("Ada", "the transaction never committed");

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
            .BeFalse("the second lease's save doesn't publish what the first lease wrote again");
    }

    // EF resets a returned context without closing a connection the app owns, so a transaction passed to
    // UseTransaction on it is still live, and the reset publishes nothing of it. The app's commit on the DbTransaction
    // raises no event, and the next read publishes it.
    [Fact]
    public async Task Returning_a_pooled_context_publishes_nothing_of_a_transaction_still_live_on_the_app_s_connection_and_a_read_after_its_commit_does()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await using var node = new SluiceNode(services: services =>
            services.AddPooledDbContextFactory<ShopDb>(
                (sp, options) => options.UseNpgsql(connection).UseSluice(sp),
                poolSize: 1
            )
        );
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var pool = node.Services.GetRequiredService<IDbContextFactory<ShopDb>>();
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var db = await pool.CreateDbContextAsync())
        {
            await db.Database.UseTransactionAsync(transaction);
            await db.Rename(ada, "Ada Lovelace");
        }

        (await adaRead.WasEvicted()).Should().BeFalse("the transaction is still live");
        (await reader.NameOf(ada)).Should().Be("Ada", "the transaction hasn't committed");

        await transaction.CommitAsync();
        (await adaRead.WasEvicted()).Should().BeTrue("the read found the transaction had ended");
    }

    // EF makes a context's DbConnection the first time anything asks for it, so a reset that asked would make one for a
    // lease that never ran a command, outside any lease, and fail where the options give no connection string yet.
    [Fact]
    public async Task Returning_a_pooled_context_that_ran_no_command_makes_no_connection()
    {
        var created = new CountConnections();
        await using var node = new SluiceNode(services: services =>
            services.AddPooledDbContextFactory<ShopDb>(
                (sp, options) =>
                    options
                        .UseNpgsql(postgres.ConnectionString)
                        .UseSluice(sp)
                        .AddInterceptors(created),
                poolSize: 1
            )
        );
        var pool = node.Services.GetRequiredService<IDbContextFactory<ShopDb>>();

        await using (var db = await pool.CreateDbContextAsync())
        {
            db.ChangeTracker.Clear();
        }

        created.Count.Should().Be(0, "the lease ran no command");
    }

    private sealed class CountConnections : DbConnectionInterceptor
    {
        public int Count { get; private set; }

        public override DbConnection ConnectionCreated(
            ConnectionCreatedEventData eventData,
            DbConnection result
        )
        {
            Count++;
            return result;
        }
    }
}
