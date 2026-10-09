using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> on a <c>UseSluice</c> context: which rows they changed is unknown, so
/// once they commit, every read of the type they wrote is evicted, and a delete also evicts what the database cascades
/// to.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BulkWriteTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ExecuteUpdate_evicts_every_read_of_the_type_it_writes_and_no_other()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var everyone = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Customers.ToListAsync(ct)
        );
        var orders = await CachedRead.OrdersOf(node.Cache, ada);

        await db
            .Customers.Where(c => c.Id == ada)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));

        (await adaRead.WasEvicted()).Should().BeTrue();
        (await bobRead.WasEvicted()).Should().BeTrue("which rows changed is unknown");
        (await everyone.WasEvicted()).Should().BeTrue();
        (await orders.WasEvicted()).Should().BeFalse("no order changed");
    }

    [Fact]
    public async Task ExecuteDelete_also_evicts_reads_of_what_the_database_cascades_to()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        db.Orders.Add(new Order { CustomerId = ada, Item = "Tea" });
        await db.SaveChangesAsync();
        var orders = await CachedRead.OrdersOf(node.Cache, ada);
        var coupons = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Coupons.ToListAsync(ct)
        );

        await db.Customers.Where(c => c.Id == ada).ExecuteDeleteAsync();

        (await orders.WasEvicted()).Should().BeTrue("deleting a customer deletes its orders");
        (await coupons.WasEvicted()).Should().BeFalse("no coupon changed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bulk_write_in_an_EF_transaction_is_evicted_once_the_transaction_ends(
        bool commit
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db
                .Customers.Where(c => c.Id == ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
            (await adaRead.WasEvicted())
                .Should()
                .BeFalse("a reader would cache the old row again before the commit");
            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
        }

        (await adaRead.WasEvicted()).Should().BeTrue("a rollback evicts too, which is safe");
        (await db.NameOf(ada)).Should().Be(commit ? "Ada Lovelace" : "Ada");
    }

    [Fact]
    public async Task A_bulk_write_in_a_TransactionScope_is_evicted_once_the_scope_completes()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(node);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node);
            await db
                .Customers.Where(c => c.Id == ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
            (await adaRead.WasEvicted()).Should().BeFalse();
            scope.Complete();
        }

        (await adaRead.WasEvicted()).Should().BeTrue();
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
    }
}
