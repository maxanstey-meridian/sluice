using System.Data.Common;
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
                "the inner transaction never committed, but disposing it publishes "
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
            .BeFalse("disposing the transaction found nothing of the leftover left on it");
    }

    public enum Abandoned
    {
        // EF began it, so disposing it raises EF's disposed event, which publishes, and closes the connection EF opened
        // for it.
        TransactionDisposed,

        // Passed in with UseTransaction, which EF raises no disposed event for. Disposing the context closes the
        // connection the app opened through EF without an event, then disposes it.
        ContextDisposed,

        // Passed in with UseTransaction. Returning a pooled context closes the connection the app opened through EF
        // without an event, but EF resets the context's services, Sluice's included; its next lease opens it.
        PooledContextLeasedAgain,

        // EF began it, on a connection the app created: disposing the context closes nothing, and EF only disposes its
        // transaction, raising its disposed event.
        ContextDisposedOnTheAppsConnection,

        // Passed in with UseTransaction, on a connection the app created, which it closes and opens again itself:
        // closing it raises no EF event, but the connection's own StateChange.
        AppClosedItsConnection,
    }

    // The tags the node invalidates, as it invalidates them, so a test can tell what published without a read: a read
    // publishes what a joined transaction that has ended holds first.
    private static SluiceNode Watched(
        out List<string> published,
        Action<IServiceCollection>? services = null
    )
    {
        var tags = new List<string>();
        published = tags;
        var node = new SluiceNode(
            cache => cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true),
            services
        );
        node.Fusion.Events.RemoveByTag += (_, e) =>
        {
            lock (tags)
            {
                tags.Add(e.Tag);
            }
        };
        return node;
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
    [InlineData(Abandoned.AppClosedItsConnection, false)]
    [InlineData(Abandoned.AppClosedItsConnection, true)]
    public async Task A_transaction_committed_outside_EF_and_left_is_evicted_when_it_is_disposed_or_its_connection_is_reset_disposed_or_closed(
        Abandoned abandoned,
        bool sync
    )
    {
        void Configure(IServiceProvider sp, DbContextOptionsBuilder options) =>
            options.UseNpgsql(postgres.ConnectionString).UseSluice(sp);
        await using var node = Watched(
            out var published,
            services => services.AddPooledDbContextFactory<ShopDb>(Configure, poolSize: 1)
        );
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var pool = node.Services.GetRequiredService<IDbContextFactory<ShopDb>>();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        var appsConnection =
            abandoned
            is Abandoned.ContextDisposedOnTheAppsConnection
                or Abandoned.AppClosedItsConnection;
        var db = abandoned switch
        {
            Abandoned.PooledContextLeasedAgain => await pool.CreateDbContextAsync(),
            _ when appsConnection => postgres.CreateDb(node, connection: connection),
            _ => postgres.CreateDb(node),
        };
        if (appsConnection)
        {
            await connection.OpenAsync();
        }
        else if (abandoned is not Abandoned.TransactionDisposed)
        {
            await db.Database.OpenConnectionAsync();
        }

        IDbContextTransaction? owned = null;
        DbTransaction transaction;
        if (
            abandoned
            is Abandoned.TransactionDisposed
                or Abandoned.ContextDisposedOnTheAppsConnection
        )
        {
            owned = await db.Database.BeginTransactionAsync();
            transaction = owned.GetDbTransaction();
        }
        else
        {
            transaction = await db.Database.GetDbConnection().BeginTransactionAsync();
            await db.Database.UseTransactionAsync(transaction);
        }

        await db.Rename(ada, "Ada Lovelace");
        await transaction.CommitAsync();
        published.Should().BeEmpty("nothing has seen the commit yet");

        if (abandoned is Abandoned.TransactionDisposed)
        {
            await Dispose(owned!, sync);
        }

        if (abandoned is Abandoned.AppClosedItsConnection)
        {
            if (sync)
            {
                connection.Close();
            }
            else
            {
                await connection.CloseAsync();
            }

            published.Should().NotBeEmpty("closing the connection ended its transaction");
            (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
            (await adaRead.WasEvicted()).Should().BeTrue();
            published.Clear();
            await connection.OpenAsync();
            await using var next = await connection.BeginTransactionAsync();
            await db.Database.UseTransactionAsync(next);
            published.Should().BeEmpty("the close took what the connection held");
            await Dispose(db, sync);
            return;
        }

        await Dispose(db, sync);
        published.Should().NotBeEmpty();
        if (abandoned is Abandoned.PooledContextLeasedAgain)
        {
            (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
            (await adaRead.WasEvicted())
                .Should()
                .BeTrue("EF reset the context, which let go of the ended transaction");
            await using var next = await pool.CreateDbContextAsync();
            next.ContextId.InstanceId.Should()
                .Be(db.ContextId.InstanceId, "the pool hands out the same instance");

            // Opened by hand, so nothing closes it before the read.
            await next.Database.OpenConnectionAsync();
            (await adaRead.WasEvicted())
                .Should()
                .BeFalse(
                    "the reset took what the connection held, so opening it again finds nothing"
                );
            return;
        }

        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    // The app begins a transaction on a connection it keeps open, a context joins it and is disposed while it's live,
    // then the app commits it on the DbTransaction: no EF event, nothing closes. The next read publishes it first, once.
    [Fact]
    public async Task A_joined_transaction_committed_outside_EF_after_its_context_is_disposed_is_evicted_by_the_next_read()
    {
        await using var node = Watched(out var published);
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var db = postgres.CreateDb(node, connection: connection))
        {
            await db.Database.UseTransactionAsync(transaction);
            await db.Rename(ada, "Ada Lovelace");
        }

        (await adaRead.WasEvicted()).Should().BeFalse("the transaction is still live");
        await transaction.CommitAsync();
        published.Should().BeEmpty("nothing has seen the commit yet");

        (await adaRead.WasEvicted()).Should().BeTrue("the read found the transaction had ended");
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeFalse("it published once");
    }

    // As above, but the app keeps its connection open and no read comes, as on a server whose reads all go elsewhere:
    // the sweep publishes it within a second, so other servers stop serving the old value then. Its timer runs only
    // while a joined transaction is watched.
    [Fact]
    public async Task A_joined_transaction_committed_outside_EF_on_a_connection_the_app_keeps_open_is_evicted_within_a_second_without_a_read()
    {
        var clock = new CountingClock();
        await using var node = Watched(
            out var published,
            services => services.AddSingleton<TimeProvider>(clock)
        );
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var db = postgres.CreateDb(node, connection: connection);
        await db.Database.UseTransactionAsync(transaction);
        clock.LiveRecurringTimers.Should().Be(0, "nothing is watched until the transaction writes");
        await db.Rename(ada, "Ada Lovelace");
        clock.LiveRecurringTimers.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        published.Should().BeEmpty("the transaction is still live");

        await transaction.CommitAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        published.Should().NotBeEmpty("the sweep found the transaction had ended");
        clock.LiveRecurringTimers.Should().Be(0, "nothing is watched any more");
        (await reader.NameOf(ada)).Should().Be("Ada Lovelace");
        (await adaRead.WasEvicted()).Should().BeTrue();

        published.Clear();
        await using var next = await connection.BeginTransactionAsync();
        await db.Database.UseTransactionAsync(next);
        await db.Rename(ada, "Ada King");
        clock
            .LiveRecurringTimers.Should()
            .Be(1, "the next joined transaction starts the sweep again");
        await next.CommitAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        published.Should().NotBeEmpty();
        clock.LiveRecurringTimers.Should().Be(0);
    }

    // Every context joining a transaction gets an EF id of its own, so a write is watched whichever of them made it.
    [Fact]
    public async Task A_write_by_the_first_of_two_contexts_joining_a_transaction_committed_outside_EF_is_evicted_within_a_second()
    {
        var clock = new CountingClock();
        await using var node = Watched(
            out var published,
            services => services.AddSingleton<TimeProvider>(clock)
        );
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var first = postgres.CreateDb(node, connection: connection);
        await using var second = postgres.CreateDb(node, connection: connection);
        await first.Database.UseTransactionAsync(transaction);
        await second.Database.UseTransactionAsync(transaction);
        await first.Rename(ada, "Ada Lovelace");

        await transaction.CommitAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        published.Should().NotBeEmpty();
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    // Npgsql hands the same DbTransaction object to the next transaction on the connection, so one the app commits and
    // begins again outside EF still looks live. A context joining the next publishes what the object held, which, for
    // contexts sharing one live transaction, only over-invalidates.
    [Fact]
    public async Task A_transaction_committed_and_begun_again_outside_EF_publishes_what_it_held_when_a_context_joins_the_next()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        var first = await connection.BeginTransactionAsync();
        await using (var db = postgres.CreateDb(node, connection: connection))
        {
            await db.Database.UseTransactionAsync(first);
            await db.Rename(ada, "Ada Lovelace");
        }

        await first.CommitAsync();
        await using var next = await connection.BeginTransactionAsync();
        next.Should().BeSameAs(first);
        (await adaRead.WasEvicted()).Should().BeFalse("the object looks live");

        await using var later = postgres.CreateDb(node, connection: connection);
        await later.Database.UseTransactionAsync(next);

        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("joining the next published what the first held");
    }

    [Fact]
    public async Task The_sweep_of_joined_transactions_holds_none_of_the_writer_s_async_state()
    {
        var clock = new CountingClock();
        await using var node = Watched(
            out _,
            services => services.AddSingleton<TimeProvider>(clock)
        );
        var writerState = new AsyncLocal<string>();
        var seen = new List<string?>();
        node.Fusion.Events.RemoveByTag += (_, _) =>
        {
            lock (seen)
            {
                seen.Add(writerState.Value);
            }
        };
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var db = postgres.CreateDb(node, connection: connection);
        await db.Database.UseTransactionAsync(transaction);
        await WriteAsync();

        await transaction.CommitAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        seen.Should().NotBeEmpty().And.AllSatisfy(state => state.Should().BeNull());

        // Its own flow, so the value doesn't flow back to the one advancing the clock.
        async Task WriteAsync()
        {
            writerState.Value = "the writer's";
            await db.Rename(ada, "Ada Lovelace");
        }
    }

    [Fact]
    public async Task The_sweep_of_joined_transactions_stops_once_its_cache_is_disposed()
    {
        var clock = new CountingClock();
        var node = Watched(out _, services => services.AddSingleton<TimeProvider>(clock));
        await using var reader = postgres.CreateDb();
        var (ada, _) = await reader.AddCustomers();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var db = postgres.CreateDb(node, connection: connection))
        {
            await db.Database.UseTransactionAsync(transaction);
            await db.Rename(ada, "Ada Lovelace");
        }

        clock.LiveRecurringTimers.Should().Be(1, "the live transaction is watched");
        await node.DisposeAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.LiveRecurringTimers.Should().Be(0);
    }

    // Npgsql's DbTransaction object belongs to the physical connection, and its pool hands that to whichever
    // DbConnection opens next, so one object runs a transaction on one DbConnection, and then the next on another.
    // Here the first is committed and disposed outside EF, on a connection the app closes itself, which publishes it
    // (StateChange), before the object can begin again on the second DbConnection, through EF, with UseTransaction or
    // outside EF, which then publishes nothing more. The second transaction's write is published only when it commits,
    // not when the first DbConnection is disposed. The first context still holds the object when the app opens its
    // connection again, on another physical connection, so its save runs outside the transaction, commits at once, and
    // publishes at once, leaving the second transaction's write to its commit.
    public enum Handover
    {
        Begun,
        Joined,
        BegunOutsideEf,
    }

    [Theory]
    [InlineData(Handover.Begun)]
    [InlineData(Handover.Joined)]
    [InlineData(Handover.BegunOutsideEf)]
    public async Task A_DbTransaction_object_Npgsql_hands_to_another_connection_published_what_it_held_when_its_first_connection_closed_and_its_next_write_only_when_that_commits(
        Handover handover
    )
    {
        await using var node = Watched(out var published);
        await using var reader = postgres.CreateDb();
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        // A pool of its own, empty, so the only idle physical connection is the one the first DbConnection left.
        var twoPhysical = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            MaxPoolSize = 2,
            ApplicationName = $"sluice-handover-{Guid.NewGuid():N}",
        }.ConnectionString;
        await using var first = new NpgsqlConnection(twoPhysical);
        await using var second = new NpgsqlConnection(twoPhysical);

        // The first context owns its connection, so disposing it disposes the connection, which EF raises an event
        // for, without opening it.
        var earlier = new ShopDb(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql(first, contextOwnsConnection: true)
                .UseSluice(node.Services)
                .Options
        );
        await first.OpenAsync();
        var transaction = await first.BeginTransactionAsync();
        await earlier.Database.UseTransactionAsync(transaction);
        await earlier.Rename(ada, "Ada Lovelace");
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        published.Should().BeEmpty("nothing has seen the commit yet");
        await first.CloseAsync();
        published.Should().NotBeEmpty("closing the connection ended its transaction");
        (await adaRead.WasEvicted()).Should().BeTrue();
        published.Clear();

        await second.OpenAsync();
        if (handover is Handover.BegunOutsideEf)
        {
            await using var outside = await second.BeginTransactionAsync();
            outside
                .Should()
                .BeSameAs(transaction, "Npgsql hands out the physical connection's object again");

            await first.OpenAsync();
            earlier.Database.AutoSavepointsEnabled = false;
            await earlier.Rename(bob, "Bob Babbage");
            (await reader.NameOf(bob))
                .Should()
                .Be("Bob Babbage", "the save ran outside the transaction");
            (await bobRead.WasEvicted())
                .Should()
                .BeTrue("the save found the object on another connection, so it published at once");
            (await adaRead.WasEvicted())
                .Should()
                .BeFalse("the close published what the first transaction held, once");
            await earlier.DisposeAsync();
            return;
        }

        await using var later = postgres.CreateDb(node, connection: second);
        if (handover is Handover.Joined)
        {
            await later.Database.UseTransactionAsync(await second.BeginTransactionAsync());
        }
        else
        {
            await later.Database.BeginTransactionAsync();
        }

        later
            .Database.CurrentTransaction!.GetDbTransaction()
            .Should()
            .BeSameAs(transaction, "Npgsql hands out the physical connection's object again");
        published.Should().BeEmpty("the close took what the object held");
        await later.Rename(bob, "Bob Babbage");

        await first.OpenAsync();
        earlier.Database.AutoSavepointsEnabled = false;
        await earlier.Rename(ada, "Ada Byron");
        (await reader.NameOf(ada)).Should().Be("Ada Byron", "the save ran outside the transaction");
        (await adaRead.WasEvicted()).Should().BeTrue("the save committed at once");
        (await bobRead.WasEvicted())
            .Should()
            .BeFalse("the save left the second connection's transaction what it holds");

        await earlier.DisposeAsync();
        (await bobRead.WasEvicted())
            .Should()
            .BeFalse("the transaction on the second connection hasn't committed");
        await later.Database.CommitTransactionAsync();

        (await reader.NameOf(bob)).Should().Be("Bob Babbage");
        (await bobRead.WasEvicted()).Should().BeTrue("the commit published the write");
        (await adaRead.WasEvicted())
            .Should()
            .BeFalse("the commit didn't publish the first transaction again");
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

    // A provider or interceptor that writes without a SaveChanges command looks like this to Sluice. What it wrote is
    // unknown, so every type in the model publishes whole: one the save never touched too.
    [Fact]
    public async Task A_save_that_reports_rows_written_without_a_command_invalidates_every_type_and_logs_an_error()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        await using var db = postgres.CreateDb(node, after: [new SuppressSave(rows: 1)]);
        var (ada, bob) = await reader.AddCustomers();
        var bobsOrders = await CachedRead.OrdersOf(node.Cache, bob);

        db.Customers.Attach(new Customer { Id = ada, Name = "Ada" }).Entity.Name = "Ada Lovelace";
        await db.SaveChangesAsync();

        (await bobsOrders.WasEvicted()).Should().BeTrue("Sluice can't tell what the save wrote");

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

    // The context that ends the transaction may have no UseSluice: EF raises its commit, rollback and failure events
    // for every context, and Sluice listens. One that has UseSluice saves before the other joins, which keeps what the
    // transaction has pending for its commit. A savepoint the owner fails to release is reported as a failed
    // transaction, which publishes what the transaction holds and keeps it for the commit.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task A_save_on_a_context_sharing_another_context_s_transaction_is_evicted_when_the_transaction_ends_whichever_context_ends_it(
        bool ownerUsesSluice,
        bool rollBack,
        bool ownerFailsARelease
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb();
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var owner = ownerUsesSluice
            ? postgres.CreateDb(node, connection: connection)
            : postgres.CreateDb(
                connection: connection,
                before: ownerFailsARelease ? [new FailReleaseSavepoint()] : []
            );
        await using var guest = postgres.CreateDb(node, connection: connection);
        await using var transaction = await owner.Database.BeginTransactionAsync();
        if (ownerUsesSluice)
        {
            await owner.Rename(ada, "Ada Lovelace");
        }

        // Joining publishes what the transaction holds early as well as when it ends, since the object may belong to
        // the next transaction on the connection (Npgsql reuses it); for a shared one that only over-invalidates.
        await guest.Database.UseTransactionAsync(transaction.GetDbTransaction());
        (await adaRead.WasEvicted())
            .Should()
            .Be(ownerUsesSluice, "the join published the owner's write");

        await guest.Rename(bob, "Bob Babbage");
        (await bobRead.WasEvicted()).Should().BeFalse("the transaction hasn't committed");

        // Contexts on connections of their own can't take the transaction up: EF refuses it after raising its used or
        // started event, and the transaction carries on.
        const string onAnotherConnection =
            "The specified transaction is not associated with the current connection.*";
        await using var joiner = postgres.CreateDb(node);
        var join = () => joiner.Database.UseTransactionAsync(transaction.GetDbTransaction());
        await join.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(onAnotherConnection);
        await using var beginner = postgres.CreateDb(
            node,
            before: [new BeginInstead(transaction.GetDbTransaction())]
        );
        var begin = () => beginner.Database.BeginTransactionAsync();
        await begin
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(onAnotherConnection);
        (await bobRead.WasEvicted())
            .Should()
            .BeFalse(
                "EF refused the transaction on the other connections, and it hasn't committed"
            );

        if (ownerFailsARelease)
        {
            await owner.Rename(ada, "Ada Lovelace");
            (await bobRead.WasEvicted())
                .Should()
                .BeTrue("the owner's savepoint failed to release, a failed transaction");
        }

        if (rollBack)
        {
            await transaction.RollbackAsync();
        }
        else
        {
            await transaction.CommitAsync();
        }

        (await reader.NameOf(bob)).Should().Be(rollBack ? "Bob" : "Bob Babbage");
        (await bobRead.WasEvicted()).Should().BeTrue();
        if (ownerUsesSluice)
        {
            (await adaRead.WasEvicted()).Should().BeTrue();
        }
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
