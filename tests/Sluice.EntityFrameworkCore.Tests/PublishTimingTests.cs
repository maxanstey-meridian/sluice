using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sluice.EntityFrameworkCore.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Chaos;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>When a tracked save publishes, and that it publishes whatever the outcome.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PublishTimingTests(PostgresFixture postgres)
{
    private static async Task<(int Ada, int Bob)> AddCustomers(ShopDb db)
    {
        var ada = new Customer { Name = "Ada" };
        var bob = new Customer { Name = "Bob" };
        db.Customers.AddRange(ada, bob);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (ada.Id, bob.Id);
    }

    private static Task<CachedRead> ReadCustomer(SluiceNode node, ShopDb reader, int id) =>
        CachedRead.Of(
            node.Cache,
            (read, ct) => read.Entity(reader.Customers, id).SingleOrDefaultAsync(ct)
        );

    private static async Task Rename(ShopDb db, int id, string name)
    {
        (await db.Customers.SingleAsync(c => c.Id == id)).Name = name;
        await db.SaveChangesAsync();
    }

    private static Task<string> NameOf(ShopDb reader, int id) =>
        reader.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).SingleAsync();

    [Fact]
    public async Task Inside_an_EF_transaction_a_save_is_evicted_only_once_the_transaction_commits()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await Rename(db, ada, "Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");

        await transaction.CommitAsync();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rolled_back_transaction_still_evicts_what_its_saves_wrote(bool sync)
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await Rename(db, ada, "Ada Lovelace");
        if (sync)
        {
            transaction.Rollback();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_commit_that_fails_still_evicts_what_its_saves_wrote(bool sync)
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, interceptors: new FailCommit());
        var (ada, _) = await AddCustomers(reader);
        var adaRead = await ReadCustomer(node, reader, ada);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await Rename(db, ada, "Ada Lovelace");
        Func<Task> commit = sync
            ? () =>
            {
                transaction.Commit();
                return Task.CompletedTask;
            }
            : () => transaction.CommitAsync();

        await commit
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The commit failed.");
        (await adaRead.WasEvicted()).Should().BeTrue("the commit's outcome is unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transaction_disposed_without_commit_evicts_nothing_even_when_its_connection_commits_later(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);
        await db.Database.OpenConnectionAsync();

        var abandoned = await db.Database.BeginTransactionAsync();
        await Rename(db, ada, "Ada Lovelace");
        var abandonedTransaction = abandoned.GetDbTransaction();
        await abandoned.DisposeAsync();
        (await adaRead.WasEvicted()).Should().BeFalse("nothing committed");

        await using var next = sync
            ? db.Database.BeginTransaction()
            : await db.Database.BeginTransactionAsync();
        next.GetDbTransaction()
            .Should()
            .BeSameAs(abandonedTransaction, "Npgsql reuses a connection's transaction object");
        await next.CommitAsync();

        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the abandoned transaction's write never committed");
        (await NameOf(reader, ada)).Should().Be("Ada");
    }

    [Fact]
    public async Task A_save_on_a_context_sharing_another_context_s_transaction_is_evicted_when_it_commits()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var (ada, _) = await AddCustomers(reader);
        var adaRead = await ReadCustomer(node, reader, ada);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        ShopDb OnSharedConnection()
        {
            var options = new DbContextOptionsBuilder<ShopDb>();
            options.UseNpgsql(connection).UseSluice(node.Services);
            return new(options.Options);
        }

        await using var owner = OnSharedConnection();
        await using var guest = OnSharedConnection();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await guest.Database.UseTransactionAsync(transaction.GetDbTransaction());

        await Rename(guest, ada, "Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");

        await transaction.CommitAsync();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transaction_the_execution_strategy_retries_is_evicted_when_its_retry_commits(
        bool failCommit
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var failOnce = new FailOnceTransiently(failCommit);
        await using var db = postgres.CreateDb(
            node,
            npgsql => npgsql.EnableRetryOnFailure(1, TimeSpan.Zero, null),
            failOnce
        );
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);
        (await db.Customers.SingleAsync(c => c.Id == ada)).Name = "Ada Lovelace";
        var attempts = 0;
        failOnce.Arm();

        // EF's pattern for a retried transaction: the changes are accepted only once it has committed.
        await db
            .Database.CreateExecutionStrategy()
            .ExecuteAsync(async () =>
            {
                if (++attempts == 2)
                {
                    // Whatever the failed attempt published, the old row is cached again before the retry.
                    await adaRead.WasEvicted();
                }

                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
                await transaction.CommitAsync();
            });
        db.ChangeTracker.AcceptAllChanges();

        attempts.Should().Be(2);
        (await NameOf(reader, ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Sync_SaveChanges_and_sync_Commit_evict_without_going_through_async_code()
    {
        // Any async call into the distributed cache throws, and FusionCache logs it as a warning.
        await using var node = new SluiceNode(cache =>
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(new SyncOnlyDistributedCache())
        );
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomers(reader);
        var adaRead = await ReadCustomer(node, reader, ada);
        node.Logs.Clear();
        var customer = db.Customers.Single(c => c.Id == ada);

        customer.Name = "Ada Lovelace";
        db.SaveChanges();
        var savedEvicted = await adaRead.WasEvicted();

        node.Logs.Clear();
        using (var transaction = db.Database.BeginTransaction())
        {
            customer.Name = "Ada King";
            db.SaveChanges();
            (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");
            node.Logs.Clear();
            transaction.Commit();
        }

        node.Logs.GetSnapshot()
            .Should()
            .NotContain(record => record.Level >= LogLevel.Warning, "the sync calls stayed sync");
        savedEvicted.Should().BeTrue();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_the_request_just_after_its_save_committed_still_evicts()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var cancelAfterCommit = new CancelAfterCommit();
        await using var db = postgres.CreateDb(node, interceptors: cancelAfterCommit);
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);
        using var request = new CancellationTokenSource();
        cancelAfterCommit.Request = request;

        (await db.Customers.SingleAsync(c => c.Id == ada)).Name = "Ada Lovelace";
        await db.SaveChangesAsync(request.Token);

        request.IsCancellationRequested.Should().BeTrue();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_the_request_just_after_its_transaction_committed_still_evicts()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var cancelAfterCommit = new CancelAfterCommit();
        await using var db = postgres.CreateDb(node, interceptors: cancelAfterCommit);
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);
        using var request = new CancellationTokenSource();

        await using var transaction = await db.Database.BeginTransactionAsync();
        await Rename(db, ada, "Ada Lovelace");
        cancelAfterCommit.Request = request;
        await transaction.CommitAsync(request.Token);

        request.IsCancellationRequested.Should().BeTrue();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    // Without a transaction and one command per batch, the first batch commits before the second runs.
    private ShopDb CreateBatchByBatchDb(SluiceNode node, params IInterceptor[] interceptors)
    {
        var db = postgres.CreateDb(node, npgsql => npgsql.MaxBatchSize(1), interceptors);
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        return db;
    }

    private static Task Save(ShopDb db, bool sync) =>
        sync ? Task.FromResult(db.SaveChanges()) : db.SaveChangesAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_that_fails_after_an_earlier_batch_committed_evicts_that_batch(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = CreateBatchByBatchDb(node);
        var (ada, _) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);

        (await db.Customers.SingleAsync(c => c.Id == ada)).Name = "Ada Lovelace";
        db.Orders.Add(new Order { CustomerId = -1, Item = "No such customer" });
        var save = () => Save(db, sync);

        await save.Should().ThrowAsync<DbUpdateException>();
        (await NameOf(reader, ada)).Should().Be("Ada Lovelace", "the first batch committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_readers_of_its_type()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = CreateBatchByBatchDb(node);
        var next = await reader.NextCustomerId();
        var notFound = await ReadCustomer(node, reader, next);
        var all = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.All(reader.Customers).ToListAsync(ct)
        );

        var customer = new Customer { Name = "Inserted" };
        db.Customers.Add(customer);
        db.Orders.Add(new Order { CustomerId = -1, Item = "No such customer" });
        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
        (await NameOf(reader, next)).Should().Be("Inserted", "the first batch committed");
        db.Entry(customer)
            .Property(c => c.Id)
            .IsTemporary.Should()
            .BeTrue("EF discards a failed save's generated keys");
        (await notFound.WasEvicted()).Should().BeTrue();
        (await all.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_concurrency_conflict_after_an_earlier_batch_committed_evicts_that_batch(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = CreateBatchByBatchDb(node);
        var (ada, bob) = await AddCustomers(db);
        var adaRead = await ReadCustomer(node, reader, ada);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();
        await reader.Customers.Where(c => c.Id == bob).ExecuteDeleteAsync();

        customers.ForEach(c => c.Name += " (edited)");
        var save = () => Save(db, sync);

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await NameOf(reader, ada)).Should().Be("Ada (edited)", "the first batch committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_save_cancelled_after_an_earlier_batch_committed_evicts_that_batch()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var setup = postgres.CreateDb();
        var (ada, bob) = await AddCustomers(setup);
        var adaRead = await ReadCustomer(node, reader, ada);
        using var request = new CancellationTokenSource();
        await using var db = CreateBatchByBatchDb(node, new CancelAfterFirstCommand(request));

        // Attached, not queried, so the save's first batch is the first command.
        Customer[] customers = [new() { Id = ada, Name = "Ada" }, new() { Id = bob, Name = "Bob" }];
        db.AttachRange(customers);
        Array.ForEach(customers, c => c.Name += " (edited)");
        var save = () => db.SaveChangesAsync(request.Token);

        await save.Should().ThrowAsync<OperationCanceledException>();
        (await NameOf(reader, ada)).Should().Be("Ada (edited)", "the first batch committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_to_invalidate_after_commit_is_logged_and_the_write_returns_normally()
    {
        var down = new ChaosDistributedCache(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()))
        );
        down.SetAlwaysThrow();
        await using var node = new SluiceNode(cache =>
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(down)
                .WithOptions(options =>
                    options.TagsDefaultEntryOptions.ReThrowDistributedCacheExceptions = true
                )
        );
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomers(reader);
        var customer = await db.Customers.SingleAsync(c => c.Id == ada);

        customer.Name = "Async";
        await db.SaveChangesAsync();
        (await NameOf(reader, ada)).Should().Be("Async");

        customer.Name = "Sync";
        db.SaveChanges();
        (await NameOf(reader, ada)).Should().Be("Sync");

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            customer.Name = "Committed";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await NameOf(reader, ada)).Should().Be("Committed");

        Task completed;
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scoped = postgres.CreateDb(node);
            await Rename(scoped, ada, "Completed");
            completed = Completion.Of(Transaction.Current!);
            scope.Complete();
        }

        await completed;
        (await NameOf(reader, ada)).Should().Be("Completed");

        node.Logs.GetSnapshot()
            .Where(record =>
                record.Category == "Sluice.EntityFrameworkCore.SluiceInterceptor"
                && record.Level == LogLevel.Error
            )
            .Should()
            .HaveCount(4)
            .And.OnlyContain(record => record.Exception != null);
    }
}
