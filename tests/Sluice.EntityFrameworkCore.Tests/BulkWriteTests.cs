using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Sluice.EntityFrameworkCore.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> on a <c>UseSluice</c> context: which rows they changed is unknown, so
/// once they commit, every read of the type they wrote is evicted, and a delete also evicts what the database cascades
/// to.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BulkWriteTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteUpdate_evicts_every_read_of_the_type_it_writes_and_no_other(bool sync)
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

        await Update(
            db.Customers.Where(c => c.Id == ada),
            set => set.SetProperty(c => c.Name, "Ada Lovelace"),
            sync
        );

        (await adaRead.WasEvicted()).Should().BeTrue();
        (await bobRead.WasEvicted()).Should().BeTrue("which rows changed is unknown");
        (await everyone.WasEvicted()).Should().BeTrue();
        (await orders.WasEvicted()).Should().BeFalse("no order changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteDelete_also_evicts_reads_of_what_the_database_cascades_to(bool sync)
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

        await Delete(db.Customers.Where(c => c.Id == ada), sync);

        (await orders.WasEvicted()).Should().BeTrue("deleting a customer deletes its orders");
        (await coupons.WasEvicted()).Should().BeFalse("no coupon changed");
    }

    [Fact]
    public async Task A_bulk_update_that_changes_a_unique_value_evicts_readers_of_any_value()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (before, after) = ($"bulk-{Guid.NewGuid():N}", $"bulk-{Guid.NewGuid():N}");
        var page = new Page { Slug = before, Section = $"bulk-{Guid.NewGuid():N}" };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        Task<CachedRead> BySlug(string slug) =>
            CachedRead.Of(
                node.Cache,
                (read, ct) =>
                    read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug).SingleOrDefaultAsync(ct)
            );
        var oldReaders = await BySlug(before);
        var newReaders = await BySlug(after);
        var orders = await CachedRead.OrdersOf(node.Cache, -1);

        await Update(
            db.Pages.Where(p => p.Id == page.Id),
            set => set.SetProperty(p => p.Slug, after),
            sync: false
        );

        (await oldReaders.WasEvicted()).Should().BeTrue();
        (await newReaders.WasEvicted()).Should().BeTrue("which values it wrote is unknown");
        (await orders.WasEvicted()).Should().BeFalse("no order changed");
    }

    [Fact]
    public async Task A_bulk_update_that_moves_rows_to_another_parent_evicts_Children_readers_of_both_parents()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        db.Orders.Add(new Order { CustomerId = ada, Item = "Tea" });
        await db.SaveChangesAsync();
        var adasOrders = await CachedRead.OrdersOf(node.Cache, ada);
        var bobsOrders = await CachedRead.OrdersOf(node.Cache, bob);
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        await db
            .Orders.Where(o => o.CustomerId == ada)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.CustomerId, bob));

        (await adasOrders.WasEvicted()).Should().BeTrue("Ada's order left her");
        (await bobsOrders.WasEvicted()).Should().BeTrue("Bob gained Ada's order");
        (await adaRead.WasEvicted()).Should().BeFalse("no customer changed");
    }

    // Npgsql reports the rows the statement itself changed, and the database cascades from each row a delete deletes,
    // so a delete of none cascades to none.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_bulk_write_that_changed_no_rows_evicts_nothing_and_one_that_changed_a_row_evicts(
        bool delete,
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        db.Orders.Add(new Order { CustomerId = ada, Item = "Tea" });
        await db.SaveChangesAsync();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var everyone = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Customers.ToListAsync(ct)
        );
        var orders = await CachedRead.OrdersOf(node.Cache, ada);
        Task<int> Write(IQueryable<Customer> rows) =>
            delete
                ? Delete(rows, sync)
                : Update(rows, set => set.SetProperty(c => c.Name, "Ada Lovelace"), sync);

        (await Write(db.Customers.Where(c => c.Id == -1))).Should().Be(0, "no customer matched");
        (await adaRead.WasEvicted()).Should().BeFalse("no row changed");
        (await everyone.WasEvicted()).Should().BeFalse("no row changed");
        (await orders.WasEvicted()).Should().BeFalse("no row changed, so nothing cascaded");

        (await Write(db.Customers.Where(c => c.Id == ada))).Should().Be(1);
        (await adaRead.WasEvicted()).Should().BeTrue();
        (await everyone.WasEvicted()).Should().BeTrue();
        (await orders.WasEvicted()).Should().Be(delete, "deleting Ada deletes her orders");
    }

    [Fact]
    public async Task A_bulk_write_that_changed_no_rows_in_a_transaction_evicts_nothing_at_its_commit()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var everyone = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Customers.ToListAsync(ct)
        );

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db
                .Customers.Where(c => c.Id == -1)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Nobody"));
            await db.Customers.Where(c => c.Id == -1).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }

        (await everyone.WasEvicted()).Should().BeFalse("no row changed");
        (await db.NameOf(ada)).Should().Be("Ada");
    }

    [Fact]
    public async Task Sync_ExecuteUpdate_and_ExecuteDelete_evict_without_going_through_async_code()
    {
        // Any async call into the distributed cache throws, and FusionCache logs it as a warning.
        await using var node = new SluiceNode(cache =>
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(new SyncOnlyDistributedCache())
        );
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        node.Logs.Clear();
        db.Customers.Where(c => c.Id == ada)
            .ExecuteUpdate(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
        node.Logs.GetSnapshot()
            .Should()
            .NotContain(record => record.Level >= LogLevel.Warning, "ExecuteUpdate stayed sync");
        (await adaRead.WasEvicted()).Should().BeTrue();

        node.Logs.Clear();
        db.Customers.Where(c => c.Id == ada).ExecuteDelete();
        node.Logs.GetSnapshot()
            .Should()
            .NotContain(record => record.Level >= LogLevel.Warning, "ExecuteDelete stayed sync");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sync_bulk_write_that_fails_or_is_cancelled_after_it_ran_evicts_without_going_through_async_code(
        bool cancel
    )
    {
        // Any async call into the distributed cache throws, and FusionCache logs it as a warning.
        await using var node = new SluiceNode(cache =>
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(new SyncOnlyDistributedCache())
        );
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, before: [new FailAfterBulkWrite(cancel)]);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        node.Logs.Clear();
        var update = () =>
            db
                .Customers.Where(c => c.Id == ada)
                .ExecuteUpdate(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
        if (cancel)
        {
            update.Should().Throw<OperationCanceledException>();
        }
        else
        {
            update.Should().Throw<InvalidOperationException>();
        }

        node.Logs.GetSnapshot()
            .Should()
            .NotContain(
                record => record.Level >= LogLevel.Warning,
                cancel ? "CommandCanceled stayed sync" : "CommandFailed stayed sync"
            );
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace", "the statement committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_bulk_write_that_fails_or_is_cancelled_after_it_ran_is_still_evicted(
        bool cancel,
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, before: [new FailAfterBulkWrite(cancel)]);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        var update = () =>
            Update(
                db.Customers.Where(c => c.Id == ada),
                set => set.SetProperty(c => c.Name, "Ada Lovelace"),
                sync
            );

        if (cancel)
        {
            await update.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await update.Should().ThrowAsync<InvalidOperationException>();
        }

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace", "the statement committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_bulk_write_to_a_type_whose_sibling_reads_refuse_is_evicted(
        bool delete,
        bool inTransaction
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var stored = new StoredPromotion { Code = "SPRING" };
        db.Add(stored);
        await db.SaveChangesAsync();
        var promotions = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Db<ShopDb>()
                    .Set<StoredPromotion>()
                    .Where(p => p.Id == stored.Id)
                    .ToListAsync(ct)
        );

        await using var transaction = inTransaction
            ? await db.Database.BeginTransactionAsync()
            : null;
        var rows = db.Set<StoredPromotion>().Where(p => p.Id == stored.Id);
        if (delete)
        {
            await rows.ExecuteDeleteAsync();
        }
        else
        {
            await rows.ExecuteUpdateAsync(set => set.SetProperty(p => p.Code, "SUMMER"));
        }

        if (transaction is not null)
        {
            (await promotions.WasEvicted()).Should().BeFalse("the transaction hasn't committed");
            await transaction.CommitAsync();
        }

        (await promotions.WasEvicted()).Should().BeTrue();
        node.Logs.GetSnapshot().Should().NotContain(record => record.Level >= LogLevel.Error);
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
        await using var reader = postgres.CreateDb();
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

    // Npgsql leaves a lowercase name unquoted, so it must stand alone; a quoted one carries its own delimiters.
    [Theory]
    [InlineData("""DELETE FROM users AS u WHERE u."Id" = @id""", "users", true)]
    [InlineData("""UPDATE public.users SET "Name" = @name""", "users", true)]
    [InlineData("""DELETE FROM users AS u""", "user", false)]
    [InlineData("""DELETE FROM "Orders" AS o WHERE o.user_id = @id""", "user", false)]
    [InlineData("""DELETE FROM old_users AS o""", "users", false)]
    [InlineData("""DELETE FROM app$users AS a""", "users", false)]
    [InlineData("""DELETE FROM "Users" AS u""", "\"Users\"", true)]
    [InlineData("""DELETE FROM "OldUsers" AS u""", "\"Users\"", false)]
    public void A_bulk_write_names_an_unquoted_table_only_as_a_whole_identifier(
        string sql,
        string table,
        bool names
    ) => ModelSources.NamePattern(table).IsMatch(sql).Should().Be(names);

    private static Task<int> Update<T>(
        IQueryable<T> rows,
        Action<UpdateSettersBuilder<T>> set,
        bool sync
    ) => sync ? Task.FromResult(rows.ExecuteUpdate(set)) : rows.ExecuteUpdateAsync(set);

    private static Task<int> Delete<T>(IQueryable<T> rows, bool sync) =>
        sync ? Task.FromResult(rows.ExecuteDelete()) : rows.ExecuteDeleteAsync();
}
