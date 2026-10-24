using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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
    [Fact]
    public async Task Without_a_transaction_a_save_is_evicted_only_once_its_write_has_run()
    {
        await using var node = new SluiceNode();
        var probe = new ProbeBeforeUpdate();
        await using var db = postgres.CreateDb(node, before: [probe]);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        bool? evictedBeforeTheWrite = null;

        // A reader that gets in before the write would cache the old row again after an early publish.
        probe.Probe = async () => evictedBeforeTheWrite = await adaRead.WasEvicted();
        await db.Rename(ada, "Ada Lovelace");

        evictedBeforeTheWrite.Should().BeFalse("nothing has been written yet");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_save_an_interceptor_makes_from_inside_another_save_evicts_what_both_saves_wrote(
        bool whileSaving,
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var saveAgain = new SaveAgain(whileSaving);

        // Either way the second save runs inside the first after Sluice captured it. One that saves from SavingChanges
        // nests only when added after Sluice; added before, it saves before Sluice captures anything.
        await using var db = whileSaving
            ? postgres.CreateDb(node, after: [saveAgain])
            : postgres.CreateDb(node, before: [saveAgain]);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();

        customers.Single(c => c.Id == ada).Name = "Ada Lovelace";
        saveAgain.Once(() => customers.Single(c => c.Id == bob).Name = "Bob Babbage");
        await Save(db, sync);

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await reader.NameOf(bob)).Should().Be("Bob Babbage");
        (await adaRead.WasEvicted()).Should().BeTrue("the outer save wrote Ada");
        (await bobRead.WasEvicted()).Should().BeTrue("the inner save wrote Bob");
    }

    [Fact]
    public async Task A_committed_save_is_evicted_though_an_interceptor_then_saves_again_in_a_transaction_it_never_commits()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var saveAgain = new SaveAgain(whileSaving: false);
        await using var db = postgres.CreateDb(node, before: [saveAgain]);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();

        customers.Single(c => c.Id == ada).Name = "Ada Lovelace";
        saveAgain.Once(
            () => customers.Single(c => c.Id == bob).Name = "Bob (never committed)",
            inAbandonedTransaction: true
        );
        await db.SaveChangesAsync();

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace", "the outer save committed");
        (await reader.NameOf(bob)).Should().Be("Bob");
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the outer save committed before the inner save's transaction began");
        (await bobRead.WasEvicted())
            .Should()
            .BeTrue(
                "the inner transaction never committed, but disposing it closed its connection, which publishes "
                    + "what it held in case it was committed outside EF; that only over-invalidates"
            );
    }

    [Fact]
    public async Task Inside_an_EF_transaction_a_save_an_interceptor_saves_again_after_is_evicted_once_more_when_the_transaction_commits()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var saveAgain = new SaveAgain(whileSaving: false);
        await using var db = postgres.CreateDb(node, before: [saveAgain]);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        customers.Single(c => c.Id == ada).Name = "Ada Lovelace";
        saveAgain.Once(() => customers.Single(c => c.Id == bob).Name = "Bob Babbage");
        await db.SaveChangesAsync();

        // The inner save published Ada's save early, as a leftover, before the commit; reading Ada now caches her old,
        // committed row again.
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the leftover published at once");
        await transaction.CommitAsync();

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue("the commit publishes the leftover again");
        (await bobRead.WasEvicted()).Should().BeTrue("the commit publishes the inner save");
    }

    [Fact]
    public async Task A_save_whose_transaction_an_interceptor_commits_before_Sluice_sees_the_save_complete_is_evicted()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, before: [new TransactionPerSave()]);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        await db.Rename(ada, "Ada Lovelace");

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Inside_an_EF_transaction_a_save_is_evicted_only_once_the_transaction_commits()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");

        await transaction.CommitAsync();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_rolled_back_transaction_still_evicts_what_its_saves_wrote()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");
        await transaction.RollbackAsync();

        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_commit_that_fails_still_evicts_what_its_saves_wrote()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, before: [new FailCommit()]);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");
        var commit = () => transaction.CommitAsync();

        await commit
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The commit failed.");
        (await adaRead.WasEvicted()).Should().BeTrue("the commit's outcome is unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_savepoint_that_fails_to_release_does_not_stop_the_commit_evicting_every_save_in_the_transaction(
        bool sync
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, before: [new FailReleaseSavepoint()]);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        customers.Single(c => c.Id == ada).Name = "Ada Lovelace";
        await Save(db, sync);

        // EF reports the second save's savepoint as a failed transaction, and Sluice publishes what the transaction
        // wrote so far. Reading Ada caches her old row again before the commit.
        customers.Single(c => c.Id == bob).Name = "Bob Babbage";
        await Save(db, sync);
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("a failed transaction publishes what it wrote so far");
        await transaction.CommitAsync();

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue("the commit publishes the first save again");
        (await bobRead.WasEvicted()).Should().BeTrue("the commit publishes the second save");
    }

    // Sluice can't tell a transaction disposed without commit from one committed outside EF and then disposed: EF
    // raises only its transaction-disposed diagnostic for either. So what it held publishes then, which over-invalidates
    // the first, and is needed for the second.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transaction_disposed_without_commit_evicts_when_it_is_disposed(bool sync)
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await db.Database.OpenConnectionAsync();

        var abandoned = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't ended");

        await Dispose(abandoned, sync);
        (await adaRead.WasEvicted()).Should().BeTrue("disposing it published what it held");

        await using var next = await db.Database.BeginTransactionAsync();
        await next.CommitAsync();
        (await adaRead.WasEvicted()).Should().BeFalse("the next transaction wrote nothing");
        (await reader.NameOf(ada)).Should().Be("Ada");
    }

    // Npgsql refuses a command in a transaction committed on the DbTransaction itself as EF creates it, so the write
    // fails before it writes, and before EF raises any event Sluice captures a write from: the write has nothing to
    // publish. A save's start publishes what the transaction held; so does the creation of a bulk write's command, which
    // has no start. Without savepoints, since EF making one there fails and is reported as a failed transaction, which
    // publishes too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_after_its_transaction_was_committed_outside_EF_evicts_what_the_transaction_wrote(
        bool bulk
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        db.Database.AutoSavepointsEnabled = false;
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        customers.Single(c => c.Id == ada).Name = "Ada Lovelace";
        await db.SaveChangesAsync();

        await transaction.GetDbTransaction().CommitAsync();
        (await adaRead.WasEvicted()).Should().BeFalse("nothing has seen the commit yet");
        if (bulk)
        {
            var write = () =>
                db
                    .Customers.Where(c => c.Id == bob)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Bob Babbage"));
            await write
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(
                    "Transaction is already completed",
                    "Npgsql refuses the ended transaction"
                );
        }
        else
        {
            customers.Single(c => c.Id == bob).Name = "Bob Babbage";
            var save = () => db.SaveChangesAsync();
            (await save.Should().ThrowAsync<DbUpdateException>())
                .WithInnerException<InvalidOperationException>()
                .WithMessage(
                    "Transaction is already completed",
                    "Npgsql refuses the ended transaction"
                );
        }

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await reader.NameOf(bob)).Should().Be("Bob");
        (await adaRead.WasEvicted()).Should().BeTrue("the write saw the transaction had ended");
    }

    // The transaction ends after the write has run and before Sluice sees it complete, so the write and what the
    // transaction held publish then, rather than being handed to the transaction, which can't commit them again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_whose_transaction_an_interceptor_commits_outside_EF_before_Sluice_sees_it_complete_is_evicted_with_what_the_transaction_wrote(
        bool bulk
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var commit = new CommitOutsideEf();
        await using var db = postgres.CreateDb(node, before: [commit]);
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");

        commit.Arm();
        if (bulk)
        {
            await db
                .Customers.Where(c => c.Id == bob)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Bob Babbage"));
        }
        else
        {
            await db.Rename(bob, "Bob Babbage");
        }

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await reader.NameOf(bob)).Should().Be("Bob Babbage");
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the transaction had ended when the write completed");
        (await bobRead.WasEvicted()).Should().BeTrue("the transaction committed the write");
    }

    // The leftover is a concurrency conflict from before the commit. The next save starts after it, publishing the
    // leftover with what the ended transaction held, and hands it to nothing: the transaction can't commit it again.
    [Fact]
    public async Task A_save_left_over_in_a_transaction_committed_outside_EF_is_evicted_when_its_context_saves_again_and_not_kept_for_the_transaction()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        db.Database.AutoSavepointsEnabled = false;
        var (ada, bob) = await db.AddCustomers();
        var cy = new Customer { Name = "Cy" };
        reader.Customers.Add(cy);
        await reader.SaveChangesAsync();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        var customers = await db
            .Customers.Where(c => c.Id == ada || c.Id == bob || c.Id == cy.Id)
            .ToListAsync();
        var transaction = await db.Database.BeginTransactionAsync();
        customers.Single(c => c.Id == ada).Name = "Ada Lovelace";
        await db.SaveChangesAsync();
        await reader.Customers.Where(c => c.Id == cy.Id).ExecuteDeleteAsync();
        customers.Where(c => c.Id != ada).ToList().ForEach(c => c.Name += " (edited)");
        var conflict = () => db.SaveChangesAsync();
        await conflict.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await transaction.GetDbTransaction().CommitAsync();
        (await bobRead.WasEvicted()).Should().BeFalse("nothing has seen the commit yet");
        db.ChangeTracker.Clear();
        await db.SaveChangesAsync();

        (await reader.NameOf(bob)).Should().Be("Bob (edited)", "the commit kept Bob's update");
        (await adaRead.WasEvicted()).Should().BeTrue("the save saw the transaction had ended");
        (await bobRead.WasEvicted()).Should().BeTrue("the save published the leftover");
        await transaction.DisposeAsync();
        (await bobRead.WasEvicted())
            .Should()
            .BeFalse(
                "closing the transaction's connection found nothing of the leftover left on the ended transaction"
            );
    }

    public enum Abandoned
    {
        // Disposing the transaction closes the connection EF opened for it.
        TransactionDisposed,

        // Disposing the context closes the connection the app opened without an event, then disposes it.
        ContextDisposed,

        // Returning a pooled context closes the connection the app opened without an event; its next lease opens it.
        PooledContextLeasedAgain,

        // Disposing a context on a connection the app created closes nothing; EF only disposes its transaction.
        ContextDisposedOnTheAppsConnection,
    }

    [Theory]
    [InlineData(Abandoned.TransactionDisposed, false)]
    [InlineData(Abandoned.TransactionDisposed, true)]
    [InlineData(Abandoned.ContextDisposed, false)]
    [InlineData(Abandoned.ContextDisposed, true)]
    [InlineData(Abandoned.PooledContextLeasedAgain, false)]
    [InlineData(Abandoned.PooledContextLeasedAgain, true)]
    [InlineData(Abandoned.ContextDisposedOnTheAppsConnection, false)]
    [InlineData(Abandoned.ContextDisposedOnTheAppsConnection, true)]
    public async Task A_transaction_committed_outside_EF_and_left_is_evicted_when_EF_closes_reopens_or_disposes_its_connection(
        Abandoned abandoned,
        bool sync
    )
    {
        void Configure(IServiceProvider sp, DbContextOptionsBuilder options) =>
            options.UseNpgsql(postgres.ConnectionString).UseSluice(sp);
        await using var node = new SluiceNode(services: services =>
            services.AddPooledDbContextFactory<ShopDb>(Configure, poolSize: 1)
        );
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var pool = node.Services.GetRequiredService<IDbContextFactory<ShopDb>>();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        var db = abandoned switch
        {
            Abandoned.PooledContextLeasedAgain => await pool.CreateDbContextAsync(),
            Abandoned.ContextDisposedOnTheAppsConnection => postgres.CreateDb(
                node,
                connection: connection
            ),
            _ => postgres.CreateDb(node),
        };
        if (abandoned is Abandoned.ContextDisposedOnTheAppsConnection)
        {
            await connection.OpenAsync();
        }
        else if (abandoned is not Abandoned.TransactionDisposed)
        {
            await db.Database.OpenConnectionAsync();
        }

        var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");
        await transaction.GetDbTransaction().CommitAsync();
        (await adaRead.WasEvicted()).Should().BeFalse("nothing has seen the commit yet");

        if (abandoned is Abandoned.TransactionDisposed)
        {
            await Dispose(transaction, sync);
        }

        await Dispose(db, sync);
        if (abandoned is Abandoned.PooledContextLeasedAgain)
        {
            await using var next = await pool.CreateDbContextAsync();
            next.ContextId.InstanceId.Should()
                .Be(db.ContextId.InstanceId, "the pool hands out the same instance");
            await next.NameOf(ada);
        }

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    private static async Task Dispose(IAsyncDisposable disposable, bool sync)
    {
        if (sync)
        {
            ((IDisposable)disposable).Dispose();
        }
        else
        {
            await disposable.DisposeAsync();
        }
    }

    // A provider or interceptor that writes without a SaveChanges command looks like this to Sluice.
    [Fact]
    public async Task A_save_that_reports_rows_written_without_a_command_logs_an_error()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, after: [new SuppressSave(rows: 1)]);
        var (ada, _) = await reader.AddCustomers();

        db.Customers.Attach(new Customer { Id = ada, Name = "Ada" }).Entity.Name = "Ada Lovelace";
        await db.SaveChangesAsync();

        node.Logs.GetSnapshot()
            .Should()
            .ContainSingle(record =>
                record.Category == typeof(SluiceInterceptor).FullName
                && record.Level == LogLevel.Error
                && record.Id.Id == 6
            )
            .Which.Message.Should()
            .Contain("ShopDb")
            .And.Contain("1 rows");
    }

    [Fact]
    public async Task A_save_on_a_context_sharing_another_context_s_transaction_is_evicted_when_it_commits()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var owner = postgres.CreateDb(node, connection: connection);
        await using var guest = postgres.CreateDb(node, connection: connection);
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await guest.Database.UseTransactionAsync(transaction.GetDbTransaction());

        await guest.Rename(ada, "Ada Lovelace");
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
            before: [failOnce]
        );
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
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
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Sync_SaveChanges_Commit_Rollback_and_their_failures_evict_without_going_through_async_code()
    {
        // Any async call into the distributed cache throws, and FusionCache logs it as a warning.
        await using var node = new SluiceNode(cache =>
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(new SyncOnlyDistributedCache())
        );
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        void ExpectNoAsyncCall(string call)
        {
            node.Logs.GetSnapshot()
                .Should()
                .NotContain(record => record.Level >= LogLevel.Warning, "{0} stayed sync", call);
            node.Logs.Clear();
        }

        node.Logs.Clear();
        await node.Cache.InvalidateAsync([reader.Customers.Dependency(-1)]);
        node.Logs.GetSnapshot()
            .Should()
            .Contain(record => record.Level >= LogLevel.Warning, "an async call does show up");

        var customer = db.Customers.Single(c => c.Id == ada);
        node.Logs.Clear();
        customer.Name = "Ada Lovelace";
        db.SaveChanges();
        ExpectNoAsyncCall("SaveChanges");
        (await adaRead.WasEvicted()).Should().BeTrue();

        using (var transaction = db.Database.BeginTransaction())
        {
            customer.Name = "Ada King";
            db.SaveChanges();
            (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");
            node.Logs.Clear();
            transaction.Commit();
            ExpectNoAsyncCall("Commit");
        }

        (await adaRead.WasEvicted()).Should().BeTrue();

        using (var transaction = db.Database.BeginTransaction())
        {
            customer.Name = "Ada Byron";
            db.SaveChanges();
            (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't ended");
            node.Logs.Clear();
            transaction.Rollback();
            ExpectNoAsyncCall("Rollback");
        }

        (await adaRead.WasEvicted()).Should().BeTrue();

        await using (var failing = CreateBatchByBatchDb(node))
        {
            failing.Customers.Single(c => c.Id == ada).Name = "Ada Failed";
            failing.Orders.Add(new Order { CustomerId = -1, Item = "No such customer" });
            node.Logs.Clear();
            var save = () => failing.SaveChanges();
            save.Should().Throw<DbUpdateException>();
            ExpectNoAsyncCall("a failed SaveChanges");
        }

        (await adaRead.WasEvicted()).Should().BeTrue("the failed save's first batch committed");

        await using (var failingCommit = postgres.CreateDb(node, before: [new FailCommit()]))
        {
            using var transaction = failingCommit.Database.BeginTransaction();
            failingCommit.Customers.Single(c => c.Id == ada).Name = "Ada Unknown";
            failingCommit.SaveChanges();
            (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't ended");
            node.Logs.Clear();
            var commit = () => transaction.Commit();
            commit.Should().Throw<InvalidOperationException>().WithMessage("The commit failed.");
            ExpectNoAsyncCall("a failed Commit");
        }

        (await adaRead.WasEvicted()).Should().BeTrue("the failed commit's outcome is unknown");
    }

    [Fact]
    public async Task Cancelling_the_request_just_after_its_save_committed_still_evicts()
    {
        await using var node = new SluiceNode();
        var cancelAfterCommit = new CancelAfterCommit();
        await using var db = postgres.CreateDb(node, before: [cancelAfterCommit]);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
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
        var cancelAfterCommit = new CancelAfterCommit();
        await using var db = postgres.CreateDb(node, before: [cancelAfterCommit]);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var request = new CancellationTokenSource();

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Ada Lovelace");
        cancelAfterCommit.Request = request;
        await transaction.CommitAsync(request.Token);

        request.IsCancellationRequested.Should().BeTrue();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    // Without a transaction and one command per batch, the first batch commits before the second runs.
    private ShopDb CreateBatchByBatchDb(
        SluiceNode node,
        IInterceptor[]? before = null,
        IInterceptor[]? after = null
    )
    {
        var db = postgres.CreateDb(node, npgsql => npgsql.MaxBatchSize(1), before, after);
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        return db;
    }

    private static Task Save(ShopDb db, bool sync) =>
        sync ? Task.FromResult(db.SaveChanges()) : db.SaveChangesAsync();

    [Fact]
    public async Task A_save_that_fails_after_an_earlier_batch_committed_evicts_that_batch()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = CreateBatchByBatchDb(node);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        (await db.Customers.SingleAsync(c => c.Id == ada)).Name = "Ada Lovelace";
        db.Orders.Add(new Order { CustomerId = -1, Item = "No such customer" });
        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace", "the first batch committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_readers_of_its_type()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = CreateBatchByBatchDb(node);
        var next = await reader.NextCustomerId();
        var notFound = await CachedRead.Customer(node.Cache, next);
        var all = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Customers.ToListAsync(ct)
        );

        var customer = new Customer { Name = "Inserted" };
        db.Customers.Add(customer);
        db.Orders.Add(new Order { CustomerId = -1, Item = "No such customer" });
        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
        (await reader.NameOf(next)).Should().Be("Inserted", "the first batch committed");
        db.Entry(customer)
            .Property(c => c.Id)
            .IsTemporary.Should()
            .BeTrue("EF discards a failed save's generated keys");
        (await notFound.WasEvicted()).Should().BeTrue();
        (await all.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_Children_readers_of_its_new_parent()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = CreateBatchByBatchDb(node, before: [new FailInsertsInto("Parcels")]);
        var next = await reader.NextCustomerId();
        var ordersOfNext = await CachedRead.OrdersOf(node.Cache, next);

        // The customer commits first, then the order that points at it. The parcel points at the customer too
        // (through its temporary key, as it has no navigation), so it saves after the order, and fails.
        var customer = new Customer { Name = "Inserted" };
        var order = new Order { Customer = customer, Item = "Tea" };
        var parcel = new Parcel();
        db.AddRange(order, parcel);
        db.Entry(parcel).Property(p => p.CustomerId).CurrentValue = db.Entry(customer)
            .Property(c => c.Id)
            .CurrentValue;
        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
        (await reader.Orders.AnyAsync(o => o.CustomerId == next))
            .Should()
            .BeTrue("the order's batch committed");
        db.Entry(order)
            .Property(o => o.CustomerId)
            .IsTemporary.Should()
            .BeTrue(
                "EF discards a failed save's generated keys, and the foreign keys copied from them"
            );
        (await ordersOfNext.WasEvicted()).Should().BeTrue();
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
        var (ada, bob) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();
        await reader.Customers.Where(c => c.Id == bob).ExecuteDeleteAsync();

        customers.ForEach(c => c.Name += " (edited)");
        var save = () => Save(db, sync);

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await reader.NameOf(ada)).Should().Be("Ada (edited)", "the first batch committed");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_save_that_goes_on_past_a_suppressed_concurrency_conflict_evicts_what_it_commits_after_it()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var suppress = new SuppressConcurrencyConflict();
        await using var db = CreateBatchByBatchDb(node, after: [suppress]);
        var (ada, bob) = await db.AddCustomers();
        var cy = new Customer { Name = "Cy" };
        db.Customers.Add(cy);
        await db.SaveChangesAsync();
        var cyRead = await CachedRead.Customer(node.Cache, cy.Id);
        var customers = await db
            .Customers.Where(c => c.Id == ada || c.Id == bob || c.Id == cy.Id)
            .ToListAsync();
        await reader.Customers.Where(c => c.Id == bob).ExecuteDeleteAsync();

        // Sluice publishes at the conflict, before Cy's batch runs, and Cy's old row is cached again then.
        suppress.Then = () => cyRead.WasEvicted();
        customers.ForEach(c => c.Name += " (edited)");
        await db.SaveChangesAsync();

        (await reader.NameOf(cy.Id))
            .Should()
            .Be("Cy (edited)", "the save went on past Bob's conflict");
        (await cyRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_save_cancelled_after_an_earlier_batch_committed_evicts_that_batch()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var setup = postgres.CreateDb();
        var (ada, bob) = await setup.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var request = new CancellationTokenSource();
        await using var db = CreateBatchByBatchDb(
            node,
            before: [new CancelAfterFirstCommand(request)]
        );

        // Attached, not queried, so the save's first batch is the first command.
        Customer[] customers = [new() { Id = ada, Name = "Ada" }, new() { Id = bob, Name = "Bob" }];
        db.AttachRange(customers);
        Array.ForEach(customers, c => c.Name += " (edited)");
        var save = () => db.SaveChangesAsync(request.Token);

        await save.Should().ThrowAsync<OperationCanceledException>();
        (await reader.NameOf(ada)).Should().Be("Ada (edited)", "the first batch committed");
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
        var (ada, _) = await reader.AddCustomers();
        var customer = await db.Customers.SingleAsync(c => c.Id == ada);

        customer.Name = "Async";
        await db.SaveChangesAsync();
        (await reader.NameOf(ada)).Should().Be("Async");

        customer.Name = "Sync";
        db.SaveChanges();
        (await reader.NameOf(ada)).Should().Be("Sync");

        var rows = db.Customers.Where(c => c.Id == ada);
        await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Bulk async"));
        (await reader.NameOf(ada)).Should().Be("Bulk async");

        rows.ExecuteUpdate(set => set.SetProperty(c => c.Name, "Bulk sync"));
        (await reader.NameOf(ada)).Should().Be("Bulk sync");

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            customer.Name = "Committed";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await reader.NameOf(ada)).Should().Be("Committed");

        Task completed;
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scoped = postgres.CreateDb(node);
            await scoped.Rename(ada, "Completed");
            completed = Completion.Of(Transaction.Current!);
            scope.Complete();
        }

        await completed;
        (await reader.NameOf(ada)).Should().Be("Completed");

        node.Logs.GetSnapshot()
            .Where(record =>
                record.Category == typeof(SluiceInterceptor).FullName
                && record.Level == LogLevel.Error
                && record.Id.Id == 1
            )
            .Should()
            .HaveCount(6)
            .And.OnlyContain(record =>
                record.Exception != null
                && record.GetStructuredStateValue("DependencyCount") != null
                && record.GetStructuredStateValue("DependencyCount") != "(null)"
            );
    }
}
