using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Npgsql;
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
            (await adaRead.WasEvictedForAnotherCaller()).Should().BeFalse();
            scope.Complete();
        }

        (await adaRead.WasEvicted()).Should().BeTrue();
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
    }

    // The statement names the table, so every type stored in it publishes, whatever columns it writes. EF refuses
    // ExecuteDelete on a type sharing its table.
    [Fact]
    public async Task A_bulk_write_to_a_split_table_evicts_every_type_sharing_it()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var ada = new Customer { Name = "Ada" };
        db.Customers.Add(ada);
        await db.SaveChangesAsync();
        var code = $"door-{Guid.NewGuid():N}";
        var locker = new Locker
        {
            Label = "L1",
            OwnerId = ada.Id,
            Code = code,
            Door = new()
            {
                Label = "L1",
                Colour = "red",
                OwnerId = ada.Id,
                Code = code,
            },
        };
        db.Lockers.Add(locker);
        await db.SaveChangesAsync();
        var door = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().LockerDoors, locker.Id).SingleOrDefaultAsync(ct)
        );
        var adasDoors = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().LockerDoors, d => d.OwnerId, ada.Id).ToListAsync(ct)
        );
        var byCode = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().LockerDoors, d => d.Code, code)
                    .SingleOrDefaultAsync(ct)
        );

        await db
            .Lockers.Where(l => l.Id == locker.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(l => l.Zone, "B"));

        (await door.WasEvicted()).Should().BeTrue();
        (await adasDoors.WasEvicted()).Should().BeTrue();
        (await byCode.WasEvicted()).Should().BeTrue();
    }

    // Npgsql enlists the connection where it's opened, on the scope's thread, and the statement completes on another,
    // which can't see the scope (AmbientTransactionTests has the saves).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bulk_write_in_a_TransactionScope_without_async_flow_is_evicted_again_once_the_scope_completes(
        bool complete
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(
            $"{postgres.ConnectionString};Application Name=sluice-{Guid.NewGuid():N}"
        );
        await using var db = postgres.CreateDb(node, connection: connection);
        var evictedInScope = false;
        Task completed = Task.CompletedTask;

        await AmbientTransactionTests.OnAThreadOfItsOwn(() =>
        {
            using var scope = new TransactionScope();
            db.Customers.Where(c => c.Id == ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"))
                .GetAwaiter()
                .GetResult();
            completed = Completion.Of(Transaction.Current!);
            evictedInScope = adaRead.WasEvictedForAnotherCaller().GetAwaiter().GetResult();
            if (complete)
            {
                scope.Complete();
            }
        });

        await completed;
        evictedInScope
            .Should()
            .BeTrue("Sluice can't tell the write is in the scope, so it publishes at once");
        (await reader.NameOf(ada)).Should().Be(complete ? "Ada Lovelace" : "Ada");
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the scope's completion published the write again");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_keyed_update_evicts_that_row_s_key_and_slug_readers_and_not_another_row_s(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (page, other) = (await AddPage(db), await AddPage(db));
        var byKey = await PageByKey(node.Cache, page.Id);
        var bySlug = await PageBySlug(node.Cache, page.Slug!);
        var otherByKey = await PageByKey(node.Cache, other.Id);
        var otherBySlug = await PageBySlug(node.Cache, other.Slug!);
        var pages = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Pages.Where(p => p.Id == other.Id).ToListAsync(ct)
        );
        var orders = await CachedRead.OrdersOf(node.Cache, -1);

        (
            await Update(
                db.Pages.Where(p => p.Title == "Draft").ForKey(page.Id),
                set => set.SetProperty(p => p.Title, "Live"),
                sync
            )
        )
            .Should()
            .Be(1);

        (await byKey.WasEvicted()).Should().BeTrue();
        (await bySlug.WasEvicted()).Should().BeTrue("the slug reader recorded the row it found");
        (await otherByKey.WasEvicted()).Should().BeFalse("only that row changed");
        (await otherBySlug.WasEvicted())
            .Should()
            .BeFalse("the statement doesn't name the slug, so no row's slug changed");
        (await pages.WasEvicted()).Should().BeTrue("a plain query of the type reads every row");
        (await orders.WasEvicted()).Should().BeFalse("no order changed");
        (
            await db
                .Pages.AsNoTracking()
                .Where(p => p.Id == other.Id)
                .Select(p => p.Title)
                .SingleAsync()
        )
            .Should()
            .Be("Draft", "the statement touched only that key");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_keyed_update_whose_statement_names_a_unique_column_evicts_every_reader_by_it(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (page, other) = (await AddPage(db), await AddPage(db));
        var taken = $"bulk-{Guid.NewGuid():N}";
        var otherBySlug = await PageBySlug(node.Cache, other.Slug!);
        var nobody = await PageBySlug(node.Cache, taken);
        var otherByKey = await PageByKey(node.Cache, other.Id);

        await Update(db.Pages.ForKey(page.Id), set => set.SetProperty(p => p.Slug, taken), sync);

        (await nobody.WasEvicted()).Should().BeTrue("the row took a value no row held");
        (await otherBySlug.WasEvicted())
            .Should()
            .BeTrue(
                "which values the statement wrote is left to the SQL, so every reader by slug goes"
            );
        (await otherByKey.WasEvicted()).Should().BeFalse("only that row changed");
    }

    // Which parent a row was under is unknown, so a keyed update still evicts every list of the type's foreign keys.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_keyed_update_evicts_every_Children_list_of_the_type_and_no_other_row_s_readers(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var (adas, bobs) = (
            new Order { CustomerId = ada, Item = "Tea" },
            new Order { CustomerId = bob, Item = "Tea" }
        );
        db.Orders.AddRange(adas, bobs);
        await db.SaveChangesAsync();
        var adasOrder = await OrderByKey(node.Cache, adas.Id);
        var bobsOrder = await OrderByKey(node.Cache, bobs.Id);
        var bobsOrders = await CachedRead.OrdersOf(node.Cache, bob);
        var bobRead = await CachedRead.Customer(node.Cache, bob);

        await Update(db.Orders.ForKey(adas.Id), set => set.SetProperty(o => o.Item, "Cake"), sync);

        (await adasOrder.WasEvicted()).Should().BeTrue();
        (await bobsOrder.WasEvicted()).Should().BeFalse("only Ada's order changed");
        (await bobsOrders.WasEvicted())
            .Should()
            .BeTrue("the order's parent is unknown, so every customer's list goes");
        (await bobRead.WasEvicted()).Should().BeFalse("no customer changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_keyed_delete_evicts_its_readers_the_lists_under_it_and_what_it_cascades_into(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var (adas, bobs) = (
            new Order { CustomerId = ada, Item = "Tea" },
            new Order { CustomerId = bob, Item = "Tea" }
        );
        db.Orders.AddRange(adas, bobs);
        await db.SaveChangesAsync();
        db.OrderLines.Add(new OrderLine { OrderId = adas.Id, LineNumber = 1 });
        await db.SaveChangesAsync();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var adasOrders = await CachedRead.OrdersOf(node.Cache, ada);
        var bobsOrders = await CachedRead.OrdersOf(node.Cache, bob);
        var adasLines = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().OrderLines, l => l.OrderId, adas.Id).ToListAsync(ct)
        );
        var coupons = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Coupons.ToListAsync(ct)
        );

        (await Delete(db.Customers.Where(c => c.Name == "Ada").ForKey(ada), sync)).Should().Be(1);

        (await db.Orders.AnyAsync(o => o.Id == adas.Id)).Should().BeFalse("the database cascaded");
        (await adaRead.WasEvicted()).Should().BeTrue();
        (await adasOrders.WasEvicted()).Should().BeTrue("her orders went with her");
        (await adasLines.WasEvicted()).Should().BeTrue("their lines went with them");
        (await bobRead.WasEvicted()).Should().BeFalse("only Ada was deleted");
        (await bobsOrders.WasEvicted()).Should().BeFalse("Bob's orders are under Bob");
        (await coupons.WasEvicted()).Should().BeFalse("no coupon changed");
    }

    // Which parent a deleted row was under is unknown too.
    [Fact]
    public async Task A_keyed_delete_evicts_every_Children_list_of_the_type_and_no_other_row_s_readers()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var (adas, bobs) = (
            new Order { CustomerId = ada, Item = "Tea" },
            new Order { CustomerId = bob, Item = "Tea" }
        );
        db.Orders.AddRange(adas, bobs);
        await db.SaveChangesAsync();
        var adasOrders = await CachedRead.OrdersOf(node.Cache, ada);
        var bobsOrder = await OrderByKey(node.Cache, bobs.Id);

        await db.Orders.ForKey(adas.Id).ExecuteDeleteAsync();

        (await adasOrders.WasEvicted()).Should().BeTrue("the order left Ada's list");
        (await bobsOrder.WasEvicted()).Should().BeFalse("only Ada's order was deleted");
    }

    // A foreign key to an alternate key is known by a value the delete doesn't know, so its lists go whole.
    [Fact]
    public async Task A_keyed_delete_evicts_every_list_through_a_cascading_foreign_key_to_an_alternate_key()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (gone, kept) = (
            new Warehouse { Code = $"WH-{Guid.NewGuid():N}" },
            new Warehouse { Code = $"WH-{Guid.NewGuid():N}" }
        );
        db.Warehouses.AddRange(gone, kept);
        await db.SaveChangesAsync();
        var keptBins = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Bins, b => b.WarehouseCode, kept.Code)
                    .ToListAsync(ct)
        );
        var keptRead = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Warehouses, kept.Id).SingleAsync(ct)
        );

        await db.Warehouses.ForKey(gone.Id).ExecuteDeleteAsync();

        (await keptBins.WasEvicted()).Should().BeTrue();
        (await keptRead.WasEvicted()).Should().BeFalse("only the other warehouse was deleted");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForKeys_evicts_each_key_s_readers_and_past_100_keys_every_read_of_the_type(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var (cy, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var cyRead = await CachedRead.Customer(node.Cache, cy);

        (
            await Update(
                db.Customers.ForKeys([ada, bob]),
                set => set.SetProperty(c => c.Name, "Renamed"),
                sync
            )
        )
            .Should()
            .Be(2);
        (await adaRead.WasEvicted()).Should().BeTrue();
        (await bobRead.WasEvicted()).Should().BeTrue();
        (await cyRead.WasEvicted()).Should().BeFalse("Cy wasn't one of the keys");
        (await db.NameOf(cy)).Should().Be("Ada", "the statement touched only those keys");

        await Update(
            db.Customers.ForKeys(Enumerable.Repeat(ada, 101)),
            set => set.SetProperty(c => c.Name, "Renamed"),
            sync
        );
        (await cyRead.WasEvicted()).Should().BeFalse("a key given twice counts once");

        int[] many = [ada, .. Enumerable.Range(1, 100).Select(i => -i)];
        await Update(
            db.Customers.ForKeys(many),
            set => set.SetProperty(c => c.Name, "Renamed again"),
            sync
        );
        (await cyRead.WasEvicted()).Should().BeTrue("past 100 keys the write publishes whole");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_keyed_write_whose_other_conditions_match_no_row_evicts_nothing(
        bool delete,
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var rows = db.Customers.Where(c => c.Name == "Nobody").ForKey(ada);

        var changed = delete
            ? await Delete(rows, sync)
            : await Update(rows, set => set.SetProperty(c => c.Name, "Ada Lovelace"), sync);

        changed.Should().Be(0);
        (await adaRead.WasEvicted()).Should().BeFalse("no row changed");
        (await db.NameOf(ada)).Should().Be("Ada");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_ForKeys_runs_nothing_and_returns_0(bool delete)
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        await db.AddCustomers();
        var everyone = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Customers.ToListAsync(ct)
        );
        var rows = db.Customers.ForKeys(Array.Empty<int>());
        var set = false;

        (delete ? await rows.ExecuteDeleteAsync() : await rows.ExecuteUpdateAsync(_ => set = true))
            .Should()
            .Be(0);
        (delete ? rows.ExecuteDelete() : rows.ExecuteUpdate(_ => set = true)).Should().Be(0);

        set.Should().BeFalse("EF never built the statement");
        (await everyone.WasEvicted()).Should().BeFalse();
        (await db.Customers.CountAsync()).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_keyed_write_in_a_transaction_is_evicted_only_for_its_rows_once_the_transaction_ends(
        bool scope
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);

        if (scope)
        {
            using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using var db = postgres.CreateDb(node);
            await db
                .Customers.ForKey(ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
            (await adaRead.WasEvictedForAnotherCaller())
                .Should()
                .BeFalse("the scope hasn't completed");
            transaction.Complete();
        }
        else
        {
            await using var db = postgres.CreateDb(node);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db
                .Customers.ForKey(ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
            (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");
            await transaction.CommitAsync();
        }

        (await adaRead.WasEvicted()).Should().BeTrue();
        (await bobRead.WasEvicted()).Should().BeFalse("only Ada changed");
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
    }

    // A table the statement reads, here in a subquery to filter by, counts as written, whole, as before.
    [Fact]
    public async Task A_keyed_write_still_evicts_every_read_of_another_type_its_statement_names()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var (adas, others) = (
            new Order { CustomerId = ada, Item = "Ada" },
            new Order { CustomerId = ada, Item = "Ada" }
        );
        db.Orders.AddRange(adas, others);
        await db.SaveChangesAsync();
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var otherOrder = await OrderByKey(node.Cache, others.Id);

        (
            await db
                .Orders.Where(o => db.Customers.Any(c => c.Name == o.Item))
                .ForKey(adas.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.Item, "Cake"))
        )
            .Should()
            .Be(1);

        (await bobRead.WasEvicted()).Should().BeTrue("the statement names the customers' table");
        (await otherOrder.WasEvicted()).Should().BeFalse("only the keyed order narrows");
    }

    // EF lets an update set the primary key, which moves the row to a key whose readers found nothing. The key's column
    // is then named in the statement once more than Sluice's own condition names it, as it is by a join on another
    // table's column of that name, so such a statement evicts every read of the type.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_keyed_update_that_may_set_the_key_evicts_every_read_of_the_type(bool join)
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var order = new Order { CustomerId = ada, Item = "Tea" };
        var other = new Order { CustomerId = bob, Item = "Tea" };
        db.Orders.AddRange(order, other);
        await db.SaveChangesAsync();
        var moved = await db.Orders.MaxAsync(o => o.Id) + 1000;
        var nothingYet = await OrderByKey(node.Cache, moved);
        var otherRead = await OrderByKey(node.Cache, other.Id);

        if (join)
        {
            await db
                .Orders.Where(o => o.Customer!.Name == "Ada")
                .ForKey(order.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.Item, "Cake"));
        }
        else
        {
            await db
                .Orders.ForKey(order.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.Id, moved));
            (await db.Orders.AnyAsync(o => o.Id == moved)).Should().BeTrue();
            (await nothingYet.WasEvicted()).Should().BeTrue("the order moved to that key");
        }

        (await otherRead.WasEvicted()).Should().BeTrue("the statement may have set any row's key");
    }

    [Fact]
    public async Task ForKey_and_ForKeys_refuse_before_anything_runs()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        ((Action)(() => db.OrderLines.ForKey(1)))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("OrderLine has a composite primary key*");
        ((Action)(() => db.Customers.ForKey(1L)))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*Customer.Id must be of type Int32*but was of type Int64*");
        ((Action)(() => db.Customers.ForKeys(new object[] { 1, "2" })))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*must be of type Int32*but was of type String*");
        ((Action)(() => db.Customers.ForKeys(new object[] { 1, null! })))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("A key in the list is null.*");
        ((Action)(() => db.Unstored.ForKey(1)))
            .Should()
            .Throw<NotSupportedException>()
            .WithMessage("Unstored isn't mapped to a table*");
        ((Action)(() => new List<Customer>().AsQueryable().ForKey(1)))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("ForKey and ForKeys need an EF query of an entity type*");
        ((Action)(() => db.CustomerCoupons.ForKey(1)))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("ForKey and ForKeys need an EF query of an entity type*");
    }

    // A sync call sets the flow's own value, which would outlive it, and narrow the next bulk write to its keys.
    [Fact]
    public async Task The_keys_do_not_outlive_the_keyed_call_even_when_it_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var bobRead = await CachedRead.Customer(node.Cache, bob);

        // The setter throws before EF makes a command, so nothing takes the pin; it must not reach the next write.
        var failed = () =>
            db
                .Customers.ForKey(ada)
                .ExecuteUpdate(_ => throw new InvalidOperationException("The setter failed."));
        failed.Should().Throw<InvalidOperationException>().WithMessage("The setter failed.");

        db.Customers.Where(c => c.Id == ada)
            .ExecuteUpdate(set => set.SetProperty(c => c.Name, "Ada Lovelace"));

        (await bobRead.WasEvicted())
            .Should()
            .BeTrue("a plain bulk write evicts every read of its type");
    }

    // Npgsql hands a connection's disposed command out again while the connection stays open, so a pin the keyed
    // statement left on it, having changed no rows, must not narrow the next statement that reuses it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_keys_do_not_reach_the_next_statement_on_a_reused_command(bool sync)
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        await db.Database.OpenConnectionAsync();

        (
            await Update(
                db.Customers.Where(c => c.Name == "Nobody").ForKey(ada),
                set => set.SetProperty(c => c.Name, "Ada Lovelace"),
                sync
            )
        )
            .Should()
            .Be(0);
        (
            await Update(
                db.Customers.Where(c => c.Id == ada),
                set => set.SetProperty(c => c.Name, "Ada Lovelace"),
                sync
            )
        )
            .Should()
            .Be(1);

        (await bobRead.WasEvicted())
            .Should()
            .BeTrue("a plain bulk write evicts every read of its type");
    }

    // An app interceptor that writes in bulk while the keyed statement runs does so on the same flow: after Sluice has
    // seen the statement end, or before, from either side of it, and whether or not the keyed statement changed a row.
    [Theory]
    [InlineData("after Sluice", "changes a row")]
    [InlineData("after Sluice", "changes no rows")]
    [InlineData("before Sluice", "changes a row")]
    [InlineData("before Sluice", "changes no rows")]
    public async Task The_keys_narrow_only_the_statement_the_keyed_call_runs(
        string order,
        string keyed
    )
    {
        await using var node = new SluiceNode();
        await using var other = postgres.CreateDb(node);
        var nested = new WriteAfterBulkWrite();
        await using var db =
            order == "after Sluice"
                ? postgres.CreateDb(node, after: [nested])
                : postgres.CreateDb(node, before: [nested]);
        var (ada, bob) = await db.AddCustomers();
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        nested.Write = () =>
            other
                .Customers.Where(c => c.Id == bob)
                .ExecuteUpdate(set => set.SetProperty(c => c.Name, "Bob Dylan"));

        await db
            .Customers.Where(c => keyed == "changes a row" || c.Name == "Nobody")
            .ForKey(ada)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));

        (await other.NameOf(bob)).Should().Be("Bob Dylan");
        (await bobRead.WasEvicted()).Should().BeTrue();
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

    private static Task<int> Update<T>(
        KeyedRows<T> rows,
        Action<UpdateSettersBuilder<T>> set,
        bool sync
    )
        where T : class =>
        sync ? Task.FromResult(rows.ExecuteUpdate(set)) : rows.ExecuteUpdateAsync(set);

    private static Task<int> Delete<T>(KeyedRows<T> rows, bool sync)
        where T : class => sync ? Task.FromResult(rows.ExecuteDelete()) : rows.ExecuteDeleteAsync();

    // A page with a slug and a section no other test uses.
    private static async Task<Page> AddPage(ShopDb db)
    {
        var page = new Page
        {
            Slug = $"bulk-{Guid.NewGuid():N}",
            Title = "Draft",
            Section = $"bulk-{Guid.NewGuid():N}",
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private static Task<CachedRead> PageByKey(SluiceCache cache, int id) =>
        CachedRead.Of(
            cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Pages, id).SingleOrDefaultAsync(ct)
        );

    private static Task<CachedRead> PageBySlug(SluiceCache cache, string slug) =>
        CachedRead.Of(
            cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug).SingleOrDefaultAsync(ct)
        );

    private static Task<CachedRead> OrderByKey(SluiceCache cache, int id) =>
        CachedRead.Of(
            cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Orders, id).SingleOrDefaultAsync(ct)
        );
}
