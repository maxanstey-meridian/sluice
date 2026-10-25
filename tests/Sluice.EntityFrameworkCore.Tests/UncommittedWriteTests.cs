using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Npgsql;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// A cached read inside the caller's own transaction, after writes it hasn't committed: one that depends on them
/// throws, any other computes fresh and stores nothing. Sluice sees an EF transaction begun on a flow inside
/// <see cref="SluiceFlow.Begin"/>, and an ambient <c>TransactionScope</c> by itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UncommittedWriteTests(PostgresFixture postgres)
{
    private const string Uncommitted =
        "*which the caller's own transaction has written and not yet committed*";

    private static readonly Query<int, string> _name = new("ef-uncommitted-name");

    public enum Kind
    {
        EfTransaction,
        UsedTransaction,
        TransactionScope,
        EnlistedTransaction,
    }

    // A TransactionScope needs no box.
    [Theory]
    [InlineData(Kind.EfTransaction, true)]
    [InlineData(Kind.UsedTransaction, true)]
    [InlineData(Kind.TransactionScope, true)]
    [InlineData(Kind.TransactionScope, false)]
    [InlineData(Kind.EnlistedTransaction, true)]
    public async Task A_read_of_a_row_the_flow_s_transaction_saved_throws_caches_nothing_and_is_normal_after_commit(
        Kind kind,
        bool box
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = box ? SluiceFlow.Begin() : null;
        await using (var transaction = await Begin(node, kind))
        {
            await transaction.Db.Rename(ada, "Ada Lovelace");

            await adaRead
                .Awaiting(read => read.WasEvicted())
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage($"Query 'ef-cached-read' depends on *Customers:{ada}*{Uncommitted}");
            (await bobRead.WasEvicted()).Should().BeTrue("it computed fresh, past the cache");
            (await adaRead.WasEvictedForAnotherCaller())
                .Should()
                .BeFalse("another request reads what's committed, which the cache still holds");

            await transaction.Commit();
        }

        (await adaRead.WasEvicted()).Should().BeTrue("the commit evicted it");
        (await adaRead.WasEvicted()).Should().BeFalse("reads cache as usual again");
        (await bobRead.WasEvicted())
            .Should()
            .BeFalse("the fresh read stored nothing, so what was cached before stayed");
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        NoFlowWarnings(node)
            .Should()
            .BeEmpty("the flow has a box, or the transaction is the ambient one, which needs none");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bulk_write_in_the_flow_s_transaction_counts(bool keyed)
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        if (keyed)
        {
            await db
                .Customers.ForKey(ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
        }
        else
        {
            await db
                .Customers.Where(c => c.Id == ada)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
        }

        await adaRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(Uncommitted);
        if (keyed)
        {
            (await bobRead.WasEvicted())
                .Should()
                .BeTrue("only Ada is pending, so Bob computes fresh");
        }
        else
        {
            await bobRead
                .Awaiting(read => read.WasEvicted())
                .Should()
                .ThrowAsync<InvalidOperationException>(
                    "a plain bulk write is pending for every customer"
                );
        }
    }

    // A keyed update that leaves the slug alone is pending only for the row's key, which the reader by slug recorded
    // in its lookup, in the bypass too.
    [Fact]
    public async Task A_read_by_a_unique_column_of_a_row_the_flow_s_transaction_wrote_by_key_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var page = new Page
        {
            Slug = $"uncommitted-{Guid.NewGuid():N}",
            Section = $"uncommitted-{Guid.NewGuid():N}",
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        var bySlug = await CachedRead.Of(
            node.Cache,
            async (read, ct) =>
                await (
                    await read.EntityAsync(read.Db<ShopDb>().Pages, p => p.Slug, page.Slug, ct)
                ).SingleOrDefaultAsync(ct)
        );
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db
            .Pages.ForKey(page.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Title, "Live"));

        await bySlug
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Pages:{page.Id}*{Uncommitted}");
    }

    [Fact]
    public async Task A_value_cached_before_the_transaction_is_not_served_and_its_read_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var computes = 0;
        Task<string> Name() =>
            node
                .Cache.GetOrSetAsync(
                    _name,
                    ada,
                    async (read, ct) =>
                    {
                        computes++;
                        return await read.Entity(read.Db<ShopDb>().Customers, ada)
                            .Select(c => c.Name)
                            .SingleAsync(ct);
                    }
                )
                .AsTask();
        (await Name()).Should().Be("Ada");
        (await Name()).Should().Be("Ada");
        computes.Should().Be(1);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Rename(ada, "Ada Lovelace");

        await ((Func<Task>)Name)
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(Uncommitted);
        computes
            .Should()
            .Be(2, "the hit was bypassed, so the compute ran and its read was checked");
    }

    [Fact]
    public async Task A_concurrent_caller_without_writes_neither_waits_on_nor_fails_with_the_bypassing_caller()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mayFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        async Task<string> Read(SluiceReads read, CancellationToken ct)
        {
            var name = await read.Entity(read.Db<ShopDb>().Customers, ada)
                .Select(c => c.Name)
                .SingleAsync(ct);
            reading.TrySetResult();
            await mayFinish.Task;
            return name;
        }

        Task<string> Quick(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Customers, ada).Select(c => c.Name).SingleAsync(ct);

        Task<string> Another() =>
            Task.Run(() => node.Cache.GetOrSetAsync(_name, ada, Quick).AsTask());

        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");

        var bypassing = node.Cache.GetOrSetAsync(_name, ada, Read).AsTask();
        await reading.Task;
        Task<string> another;
        using (ExecutionContext.SuppressFlow())
        {
            another = Another();
        }

        (await another.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("Ada");
        mayFinish.SetResult();
        await bypassing
            .Awaiting(call => call)
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(Uncommitted);
    }

    // Without SluiceFlow.Begin, an EF transaction is never seen, so the read returns what's committed; so is an
    // enlisted transaction outside a scope, which isn't Transaction.Current. Sluice warns once per cache, at the first
    // write in one, and not for the transaction EF begins for a save of its own, which commits before the save
    // returns, nor for one that writes nothing.
    [Theory]
    [InlineData(Kind.EfTransaction)]
    [InlineData(Kind.UsedTransaction)]
    [InlineData(Kind.EnlistedTransaction)]
    public async Task Without_a_flow_box_a_transaction_that_is_not_the_ambient_one_is_not_checked_and_warns_once(
        Kind kind
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        await using (var plain = postgres.CreateDb(node))
        {
            // Two rows, so EF begins a transaction of its own for the save.
            foreach (
                var customer in await plain
                    .Customers.Where(c => c.Id == ada || c.Id == bob)
                    .ToListAsync()
            )
            {
                customer.Name += " Senior";
            }

            await plain.SaveChangesAsync();
            await using var readOnly = await plain.Database.BeginTransactionAsync();
            await plain.NameOf(ada);
        }

        NoFlowWarnings(node)
            .Should()
            .BeEmpty(
                "a save's own transaction commits before it returns, and the other wrote nothing"
            );
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var transaction = await Begin(node, kind);

        await transaction.Db.Rename(ada, "Ada Lovelace");
        await transaction.Db.Rename(bob, "Bob");

        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the cached value from before the save is served");
        NoFlowWarnings(node)
            .Should()
            .ContainSingle()
            .Which.Message.Should()
            .Contain("SluiceFlow.Begin()");
    }

    private static IEnumerable<FakeLogRecord> NoFlowWarnings(SluiceNode node) =>
        node
            .Logs.GetSnapshot()
            .Where(record =>
                record.Category == typeof(SluiceInterceptor).FullName
                && record.Level == LogLevel.Warning
                && record.Id.Id == 7
            );

    // A transaction disposed without commit or rollback raises no event, and committed nothing; the provider may hand
    // its DbTransaction object out again for the next one. Disposing it closes the connection EF opened for it, which
    // publishes what it held, as it would had it been committed outside EF.
    [Fact]
    public async Task A_transaction_disposed_without_commit_leaves_nothing_pending()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = SluiceFlow.Begin();

        await using (await db.Database.BeginTransactionAsync())
        {
            await db.Rename(ada, "Ada Lovelace");
        }

        db.ChangeTracker.Clear();
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("closing the connection published the write, which over-invalidates");
        (await adaRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing committed");
        await using (var next = await db.Database.BeginTransactionAsync())
        {
            await db.Rename(bob, "Bob Babbage");
            (await adaRead.WasEvicted())
                .Should()
                .BeTrue("Bob's save is pending, so Ada computes fresh");
            await next.RollbackAsync();
        }

        (await adaRead.WasEvicted()).Should().BeFalse("the rollback left nothing pending");
        (await bobRead.WasEvicted()).Should().BeTrue("a rollback evicts too");
        (await db.NameOf(ada)).Should().Be("Ada");
    }

    // A pooled context, returned to the pool, serves another request next, which may begin a transaction on it while
    // this flow still runs.
    [Fact]
    public async Task A_flow_s_probe_reports_only_its_own_transaction_not_a_later_one_on_the_same_context()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var flow = SluiceFlow.Begin();
        await using (var mine = await db.Database.BeginTransactionAsync())
        {
            await mine.CommitAsync();
        }

        var theirs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task another;
        using (ExecutionContext.SuppressFlow())
        {
            another = Task.Run(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.Rename(ada, "Ada Lovelace");
                theirs.SetResult();
                await done.Task;
            });
        }

        await theirs.Task;
        (await adaRead.WasEvicted()).Should().BeFalse("the pending write is another request's");
        done.SetResult();
        await another;
    }

    [Fact]
    public async Task A_nested_Begin_shares_the_outer_box_until_the_outermost_handle_is_disposed()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var outer = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        using (SluiceFlow.Begin())
        {
            await db.Rename(ada, "Ada Lovelace");
        }

        await adaRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>("the inner handle left the outer box in place");
        outer.Dispose();
        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("without the box, the transaction isn't seen");
    }

    // EF raises no event for a savepoint made outside it, so these go through EF. PostgreSQL keeps the savepoint it
    // rolls back to.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Writes_rolled_back_to_a_savepoint_are_no_longer_pending_and_those_before_it_still_are(
        bool sync,
        bool bulk
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(bob, "Bob Babbage");

        await Savepoint(transaction, sync);
        if (bulk)
        {
            var renameAda = db.Customers.ForKey(ada);
            _ = sync
                ? renameAda.ExecuteUpdate(set => set.SetProperty(c => c.Name, "Ada Lovelace"))
                : await renameAda.ExecuteUpdateAsync(set =>
                    set.SetProperty(c => c.Name, "Ada Lovelace")
                );
        }
        else
        {
            (await db.Customers.SingleAsync(c => c.Id == ada)).Name = "Ada Lovelace";
            _ = sync ? db.SaveChanges() : await db.SaveChangesAsync();
        }

        await RollBackToSavepoint(transaction, sync);

        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("Ada's write was undone, so her read computes fresh past Bob's pending write");
        await bobRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>("Bob's write came before the savepoint")
            .WithMessage(Uncommitted);
        await transaction.CommitAsync();
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("what was undone still publishes when the transaction ends");
        (await db.NameOf(ada)).Should().Be("Ada");
        (await db.NameOf(bob)).Should().Be("Bob Babbage");
    }

    [Fact]
    public async Task A_released_savepoint_s_writes_belong_to_the_one_around_it()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await transaction.CreateSavepointAsync("outer");
        await db.Rename(bob, "Bob Babbage");
        await transaction.CreateSavepointAsync("inner");
        await db.Rename(ada, "Ada Lovelace");
        await transaction.ReleaseSavepointAsync("inner");
        await transaction.RollbackToSavepointAsync("outer");

        (await adaRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing has ended");
        (await bobRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing has ended");
    }

    // Casebridge's unit of work names every savepoint alike. PostgreSQL's ROLLBACK TO keeps the savepoint, a new one
    // of the same name shadows it, and a release drops the newer, so the last rollback goes back to the first.
    [Fact]
    public async Task A_savepoint_name_reused_after_a_rollback_shadows_the_first()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await transaction.CreateSavepointAsync("uow_1");
        await db.Rename(ada, "Ada Lovelace");
        await transaction.RollbackToSavepointAsync("uow_1");
        await transaction.CreateSavepointAsync("uow_1");
        await db.Rename(bob, "Bob Babbage");
        await transaction.ReleaseSavepointAsync("uow_1");
        await db.Rename(ada, "Ada Byron");
        await transaction.RollbackToSavepointAsync("uow_1");

        (await adaRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing has ended");
        (await bobRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing has ended");
        await transaction.CommitAsync();
        (await db.NameOf(ada)).Should().Be("Ada");
        (await db.NameOf(bob)).Should().Be("Bob");
        (await adaRead.WasEvicted()).Should().BeTrue("the commit publishes what was undone");
        (await bobRead.WasEvicted()).Should().BeTrue("the commit publishes what was undone");
    }

    // Npgsql hands the same DbTransaction object out for the next transaction on the connection, so a save left over
    // from the first is told apart by EF's id for it. No savepoint, so nothing rolls the conflict's write back.
    [Fact]
    public async Task A_save_left_over_from_an_ended_transaction_is_not_pending_in_the_next_one_on_the_connection()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        db.Database.AutoSavepointsEnabled = false;
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await db.Database.OpenConnectionAsync();
        using var flow = SluiceFlow.Begin();
        var first = await db.Database.BeginTransactionAsync();
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();
        await reader.Customers.Where(c => c.Id == bob).ExecuteDeleteAsync();
        customers.ForEach(c => c.Name += " (edited)");
        var conflict = () => db.SaveChangesAsync();
        await conflict.Should().ThrowAsync<DbUpdateConcurrencyException>();
        var firstTransaction = first.GetDbTransaction();
        await first.DisposeAsync();

        await using var next = await db.Database.BeginTransactionAsync();
        next.GetDbTransaction().Should().BeSameAs(firstTransaction);
        db.ChangeTracker.Clear();
        db.Customers.Add(new Customer { Name = "Cy" });
        await db.SaveChangesAsync();

        (await adaRead.WasEvicted())
            .Should()
            .BeTrue(
                "Ada's leftover isn't pending, so her read computes fresh past Cy's pending insert"
            );
    }

    // EF rolls a failed save back to the savepoint it made for it; one whose earlier statements ran is undone too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_that_fails_inside_the_flow_s_transaction_leaves_nothing_pending(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var slug = $"taken-{Guid.NewGuid():N}";
        db.Pages.Add(new Page { Slug = slug, Section = $"taken-{Guid.NewGuid():N}" });
        await db.SaveChangesAsync();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        (await db.Customers.SingleAsync(c => c.Id == ada)).Name = "Ada Lovelace";
        db.Pages.Add(new Page { Slug = slug, Section = $"taken-{Guid.NewGuid():N}" });
        var save = () => sync ? Task.FromResult(db.SaveChanges()) : db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();

        (await adaRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing has ended");
        await transaction.CommitAsync();
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the commit publishes what the save may have written");
        (await db.NameOf(ada)).Should().Be("Ada");
    }

    // An interceptor after Sluice saves again from SavingChanges, so the inner save runs, and completes, before the
    // outer one makes its savepoint. The outer save then writes what another interceptor changed after, and fails.
    [Fact]
    public async Task A_save_that_fails_after_a_save_ran_inside_it_leaves_nothing_of_its_own_pending()
    {
        await using var node = new SluiceNode();
        await using var setup = postgres.CreateDb();
        var (ada, bob) = await setup.AddCustomers();
        var slug = $"taken-{Guid.NewGuid():N}";
        setup.Pages.Add(new Page { Slug = slug, Section = $"taken-{Guid.NewGuid():N}" });
        await setup.SaveChangesAsync();
        var saveAgain = new SaveAgain(whileSaving: true);
        var savings = 0;
        var changeAfter = new ChangeWhileSaving(context =>
        {
            // The inner save's turn comes first, inside the outer save's.
            if (++savings == 2)
            {
                var shop = (ShopDb)context;
                shop.Customers.Local.Single(c => c.Id == ada).Name = "Ada Lovelace";
                shop.Pages.Add(new Page { Slug = slug, Section = $"taken-{Guid.NewGuid():N}" });
            }
        });
        await using var db = postgres.CreateDb(node, after: [saveAgain, changeAfter]);
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();

        saveAgain.Once(() => customers.Single(c => c.Id == bob).Name = "Bob Babbage");
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();

        savings.Should().Be(2);
        await adaRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .NotThrowAsync("the outer save's writes were rolled back to its savepoint");
        await bobRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "the inner save's write is still in the transaction"
            )
            .WithMessage(Uncommitted);
        await transaction.CommitAsync();
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the commit publishes what the outer save may have written");
        (await db.NameOf(ada)).Should().Be("Ada");
        (await db.NameOf(bob)).Should().Be("Bob Babbage");
    }

    [Fact]
    public async Task A_concurrency_conflict_inside_the_flow_s_transaction_leaves_nothing_pending()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();
        await reader.Customers.Where(c => c.Id == bob).ExecuteDeleteAsync();

        customers.ForEach(c => c.Name += " (edited)");
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await adaRead.WasEvicted()).Should().BeFalse("nothing is pending, and nothing has ended");
        await transaction.CommitAsync();
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the commit publishes what the save may have written");
    }

    [Fact]
    public async Task A_rollback_to_a_savepoint_that_fails_leaves_its_writes_pending()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node, before: [new FailRollbackToSavepoint()]);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await transaction.CreateSavepointAsync("a");
        await db.Rename(ada, "Ada Lovelace");
        var rollBack = () => transaction.RollbackToSavepointAsync("a");
        await rollBack.Should().ThrowAsync<InvalidOperationException>();

        await adaRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>("the write is still in the transaction")
            .WithMessage(Uncommitted);
    }

    // The probe joins the box at the flow's first write in the transaction, and reports every write the transaction
    // has pending, those before the box included.
    [Theory]
    [InlineData(Kind.EfTransaction)]
    [InlineData(Kind.UsedTransaction)]
    public async Task A_transaction_begun_before_Begin_is_checked_from_its_first_write_inside_it(
        Kind kind
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        await using var transaction = await Begin(node, kind);
        await transaction.Db.Rename(bob, "Bob Babbage");

        using var flow = SluiceFlow.Begin();
        await transaction.Db.Rename(ada, "Ada Lovelace");

        await adaRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(Uncommitted);
        await bobRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>("the write before the box is pending too")
            .WithMessage(Uncommitted);
    }

    // A cached read has no context to look at, so a transaction is seen only through a write the flow makes in it
    // inside the box (the documented remedy: call Begin() before opening it).
    [Fact]
    public async Task A_transaction_written_in_only_before_Begin_is_not_checked()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");

        using var flow = SluiceFlow.Begin();

        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the cached value from before the save is served");
    }

    // Each flow's box gets its own probe of the transaction, at its own first write in it.
    [Fact]
    public async Task An_enlisted_transaction_written_in_from_two_flows_with_boxes_is_checked_in_both()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        await using var transaction = await Begin(node, Kind.EnlistedTransaction);

        async Task WriteAndRead(int id, string name, CachedRead read)
        {
            using var flow = SluiceFlow.Begin();
            await transaction.Db.Rename(id, name);
            await read.Awaiting(r => r.WasEvicted())
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(Uncommitted);
        }

        // Each on a flow of its own, without this one's box, one after the other, since they share the context.
        Task OnItsOwnFlow(Func<Task> write)
        {
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(write);
            }
        }

        await OnItsOwnFlow(() => WriteAndRead(ada, "Ada Lovelace", adaRead));
        await OnItsOwnFlow(() => WriteAndRead(bob, "Bob Babbage", bobRead));
    }

    // Committing or rolling back the DbTransaction itself raises no EF event, and EF still holds it, so Sluice sees the
    // end when it next looks: at the flow's next cached read.
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_transaction_committed_outside_EF_is_evicted_at_the_flow_s_next_read_and_leaves_nothing_pending(
        bool commit,
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");

        var raw = transaction.GetDbTransaction();
        switch (commit, sync)
        {
            case (true, true):
                raw.Commit();
                break;
            case (true, false):
                await raw.CommitAsync();
                break;
            case (false, true):
                raw.Rollback();
                break;
            default:
                await raw.RollbackAsync();
                break;
        }

        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the read saw the transaction end, and published");
        (await adaRead.WasEvicted()).Should().BeFalse("nothing is pending, so it cached again");
        (await reader.NameOf(ada)).Should().Be(commit ? "Ada Lovelace" : "Ada");
    }

    // Npgsql's DbTransaction object belongs to the physical connection, which its pool hands to whichever DbConnection
    // opens next. A context on this flow still holds the object after the app commits it outside EF and closes the
    // connection itself; another flow's transaction runs on the object now, and what that has written isn't this
    // flow's. One physical connection, in a pool of its own, so the other flow's DbConnection gets it. Begun there
    // outside EF, the object still holds this flow's transaction's record, which this flow's next read publishes, as
    // that transaction has ended.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_DbTransaction_object_Npgsql_hands_to_another_flow_s_connection_does_not_count_that_flow_s_writes_as_pending_here(
        bool outsideEf
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var onePhysical = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            MaxPoolSize = 1,
            ApplicationName = $"sluice-handover-{Guid.NewGuid():N}",
        }.ConnectionString;
        await using var first = new NpgsqlConnection(onePhysical);
        await using var second = new NpgsqlConnection(onePhysical);
        await using var earlier = postgres.CreateDb(node, connection: first);
        await using var later = postgres.CreateDb(node, connection: second);
        using var flow = SluiceFlow.Begin();
        await first.OpenAsync();
        var transaction = await first.BeginTransactionAsync();
        await earlier.Database.UseTransactionAsync(transaction);
        await earlier.Rename(ada, "Ada Lovelace");
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await first.CloseAsync();

        Task other;
        NpgsqlTransaction? outside = null;
        using (ExecutionContext.SuppressFlow())
        {
            other = Task.Run(async () =>
            {
                await second.OpenAsync();
                if (outsideEf)
                {
                    outside = await second.BeginTransactionAsync();
                    return;
                }

                await later.Database.BeginTransactionAsync();
                await later.Rename(bob, "Bob Babbage");
            });
        }

        await other;
        if (outsideEf)
        {
            outside
                .Should()
                .BeSameAs(transaction, "Npgsql hands out the physical connection's object again");
            (await adaRead.WasEvicted())
                .Should()
                .BeTrue(
                    "the read found the object on another connection, so this flow's transaction had ended"
                );
            await outside!.CommitAsync();
            return;
        }

        later
            .Database.CurrentTransaction!.GetDbTransaction()
            .Should()
            .BeSameAs(transaction, "Npgsql hands out the physical connection's object again");
        (await bobRead.WasEvicted())
            .Should()
            .BeFalse("the other flow's write isn't this flow's, and hasn't committed");
        await later.Database.CommitTransactionAsync();
        (await bobRead.WasEvicted()).Should().BeTrue("the other flow's commit published it");
    }

    // A failed statement aborts a PostgreSQL transaction without ending it: it holds its writes until it's rolled back.
    [Fact]
    public async Task A_failed_statement_in_the_flow_s_transaction_keeps_its_writes_pending()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Database.AutoSavepointsEnabled = false;
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var flow = SluiceFlow.Begin();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");

        var fail = () => db.Database.ExecuteSqlRawAsync("select 1 / 0");
        await fail.Should().ThrowAsync<PostgresException>();

        await adaRead
            .Awaiting(read => read.WasEvicted())
            .Should()
            .ThrowAsync<InvalidOperationException>("the aborted transaction hasn't ended")
            .WithMessage(Uncommitted);
    }

    private static Task Savepoint(IDbContextTransaction transaction, bool sync)
    {
        if (!sync)
        {
            return transaction.CreateSavepointAsync("a");
        }

        transaction.CreateSavepoint("a");
        return Task.CompletedTask;
    }

    private static Task RollBackToSavepoint(IDbContextTransaction transaction, bool sync)
    {
        if (!sync)
        {
            return transaction.RollbackToSavepointAsync("a");
        }

        transaction.RollbackToSavepoint("a");
        return Task.CompletedTask;
    }

    // Not async, so a TransactionScope it creates is the caller's ambient transaction, as it would be in the caller.
    private Task<Open> Begin(SluiceNode node, Kind kind)
    {
        switch (kind)
        {
            case Kind.TransactionScope:
            {
                var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
                return Task.FromResult(
                    new Open(
                        postgres.CreateDb(node),
                        () =>
                        {
                            scope.Complete();
                            scope.Dispose();
                            return Task.CompletedTask;
                        },
                        scope.Dispose
                    )
                );
            }
            case Kind.EnlistedTransaction:
                return Enlisted();
            case Kind.UsedTransaction:
                return Used();
            default:
                return Ef();
        }

        async Task<Open> Ef()
        {
            var db = postgres.CreateDb(node);
            var transaction = await db.Database.BeginTransactionAsync();
            return new(db, () => transaction.CommitAsync(), transaction.Dispose);
        }

        // Begun outside EF, then joined, so EF reports only that the context uses it.
        async Task<Open> Used()
        {
            var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            var db = postgres.CreateDb(node, connection: connection);
            await db.Database.UseTransactionAsync(transaction);
            return new(
                db,
                () => db.Database.CommitTransactionAsync(),
                () =>
                {
                    transaction.Dispose();
                    connection.Dispose();
                }
            );
        }

        async Task<Open> Enlisted()
        {
            var transaction = new CommittableTransaction();
            var db = postgres.CreateDb(node);
            await db.Database.OpenConnectionAsync();
            db.Database.EnlistTransaction(transaction);
            return new(
                db,
                () =>
                {
                    transaction.Commit();
                    return Task.CompletedTask;
                },
                transaction.Dispose
            );
        }
    }

    // A transaction open on a context of the node's, with what commits it and what disposes it. Disposing is sync, on
    // the caller's flow, as a TransactionScope must be.
    private sealed class Open(ShopDb db, Func<Task> commit, Action dispose) : IAsyncDisposable
    {
        public ShopDb Db => db;

        public Task Commit() => commit();

        public ValueTask DisposeAsync()
        {
            dispose();
            return db.DisposeAsync();
        }
    }
}
