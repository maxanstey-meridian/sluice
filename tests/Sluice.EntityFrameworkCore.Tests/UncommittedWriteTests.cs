using System.Transactions;
using Microsoft.EntityFrameworkCore;
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
                .WithMessage($"Query 'ef-cached-read' read *Customer:{ada}*{Uncommitted}");
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
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, page.Slug)
                    .SingleOrDefaultAsync(ct)
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
            .WithMessage($"*Page:{page.Id}*{Uncommitted}");
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

    // Without SluiceFlow.Begin, an EF transaction is never seen, so the read returns what's committed (the documented
    // gap); so is an enlisted transaction outside a scope, which isn't Transaction.Current.
    [Theory]
    [InlineData(Kind.EfTransaction)]
    [InlineData(Kind.UsedTransaction)]
    [InlineData(Kind.EnlistedTransaction)]
    public async Task Without_a_flow_box_a_transaction_that_is_not_the_ambient_one_is_not_checked(
        Kind kind
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var transaction = await Begin(node, kind);

        await transaction.Db.Rename(ada, "Ada Lovelace");

        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the cached value from before the save is served");
    }

    // A transaction disposed without commit or rollback raises no event, and committed nothing; the provider may hand
    // its DbTransaction object out again for the next one.
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
