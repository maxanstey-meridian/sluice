using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sluice.EntityFrameworkCore.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// When a save inside an enlisted or ambient (<c>TransactionScope</c>) transaction publishes. A compute reads on its own
/// context with the ambient transaction suppressed, so cached reads run inside a scope too.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AmbientTransactionTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inside_a_TransactionScope_a_save_is_evicted_only_once_the_scope_completes_whatever_its_outcome(
        bool complete
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        Task completed;

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node);
            await db.Rename(ada, "Ada Lovelace");
            completed = Completion.Of(Transaction.Current!);
            (await adaRead.WasEvictedForAnotherCaller())
                .Should()
                .BeFalse("the scope hasn't completed");

            if (complete)
            {
                scope.Complete();
            }
        }

        await completed;
        (await reader.NameOf(ada)).Should().Be(complete ? "Ada Lovelace" : "Ada");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inside_an_enlisted_transaction_a_save_is_evicted_only_once_it_completes_whatever_its_outcome(
        bool commit
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        using var transaction = new CommittableTransaction();
        await using var db = postgres.CreateDb(node);
        await db.Database.OpenConnectionAsync();
        db.Database.EnlistTransaction(transaction);

        await db.Rename(ada, "Ada Lovelace");
        var completed = Completion.Of(transaction);
        (await adaRead.WasEvicted()).Should().BeFalse("the transaction hasn't completed");

        if (commit)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }

        await completed;
        (await reader.NameOf(ada)).Should().Be(commit ? "Ada Lovelace" : "Ada");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Contexts_saving_inside_one_TransactionScope_are_all_evicted_when_it_completes()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        var bobRead = await CachedRead.Customer(node.Cache, bob);
        Task completed;

        // One connection, so the scope needs no distributed transaction.
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await connection.OpenAsync();
            await using var first = postgres.CreateDb(node, connection: connection);
            await using var second = postgres.CreateDb(node, connection: connection);
            await first.Rename(ada, "Ada Lovelace");
            await second.Rename(bob, "Bob Babbage");
            completed = Completion.Of(Transaction.Current!);
            (await adaRead.WasEvictedForAnotherCaller())
                .Should()
                .BeFalse("the scope hasn't completed");
            (await bobRead.WasEvictedForAnotherCaller())
                .Should()
                .BeFalse("the scope hasn't completed");

            scope.Complete();
        }

        await completed;
        (await adaRead.WasEvicted()).Should().BeTrue();
        (await bobRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task With_Enlist_false_a_save_inside_a_TransactionScope_is_evicted_at_once_and_outlives_the_scope()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node, enlist: false);
            await db.Rename(ada, "Ada Lovelace");

            (await adaRead.WasEvicted()).Should().BeTrue("EF committed the save at once");
        }

        (await reader.NameOf(ada))
            .Should()
            .Be("Ada Lovelace", "the aborted scope didn't hold the save");
    }

    [Fact]
    public async Task A_save_left_behind_by_a_conflict_in_a_disposed_TransactionScope_is_evicted_by_the_next_save_without_failing_it()
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var db = postgres.CreateDb(node);
        Task completed;

        var customers = await db.Customers.Where(c => c.Id == ada || c.Id == bob).ToListAsync();
        await reader.Customers.Where(c => c.Id == bob).ExecuteDeleteAsync();
        customers.ForEach(c => c.Name += " (edited)");

        // The conflict ends the save without an event, so Sluice still holds it when the scope is disposed.
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
            completed = Completion.Of(Transaction.Current!);
        }

        await completed;
        (await adaRead.WasEvicted()).Should().BeTrue("the conflict published when the scope ended");

        // Ada's row is cached again, so only the leftover's own publish can evict it.
        db.ChangeTracker.Clear();
        db.Customers.Add(new Customer { Name = "Cy" });
        await db.SaveChangesAsync();

        (await adaRead.WasEvicted()).Should().BeTrue("the next save published the leftover");
        node.Logs.GetSnapshot().Should().NotContain(record => record.Level >= LogLevel.Error);
    }

    public enum HiddenScope
    {
        WarmPool,
        ColdPool,
        ColdPoolSeveralBatches,
        OpenedOnTheScopeThreadSavedFromAnother,
    }

    // A TransactionScope without TransactionScopeAsyncFlowOption.Enabled is in Transaction.Current on the thread that
    // made it only, so an async save blocked on there completes on another thread, which can't see it. On a cold
    // connection pool, Npgsql enlists after its first await, so even the save's first command can't see it; only the
    // connection's opening can. Sluice publishes such a save at once, and again when the scope completes.
    [Theory]
    [InlineData(HiddenScope.WarmPool, true)]
    [InlineData(HiddenScope.WarmPool, false)]
    [InlineData(HiddenScope.ColdPool, true)]
    [InlineData(HiddenScope.ColdPool, false)]
    [InlineData(HiddenScope.ColdPoolSeveralBatches, true)]
    [InlineData(HiddenScope.ColdPoolSeveralBatches, false)]
    [InlineData(HiddenScope.OpenedOnTheScopeThreadSavedFromAnother, true)]
    [InlineData(HiddenScope.OpenedOnTheScopeThreadSavedFromAnother, false)]
    public async Task Inside_a_TransactionScope_without_async_flow_an_async_save_is_evicted_again_once_the_scope_completes(
        HiddenScope setup,
        bool complete
    )
    {
        await using var node = new SluiceNode();
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, bob) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await using var connection = new NpgsqlConnection(
            setup is HiddenScope.WarmPool
                ? postgres.ConnectionString
                : $"{postgres.ConnectionString};Application Name=sluice-{Guid.NewGuid():N}"
        );
        await using var db = postgres.CreateDb(
            node,
            npgsql =>
            {
                if (setup is HiddenScope.ColdPoolSeveralBatches)
                {
                    npgsql.MaxBatchSize(1);
                }
            },
            connection: connection
        );
        Customer[] customers = [new() { Id = ada, Name = "Ada" }, new() { Id = bob, Name = "Bob" }];
        db.AttachRange(setup is HiddenScope.ColdPoolSeveralBatches ? customers : customers[..1]);
        Array.ForEach(customers, c => c.Name += " (edited)");
        var evictedInScope = false;
        Task completed = Task.CompletedTask;

        await OnAThreadOfItsOwn(() =>
        {
            using var scope = new TransactionScope();
            if (setup is HiddenScope.OpenedOnTheScopeThreadSavedFromAnother)
            {
                db.Database.OpenConnectionAsync().GetAwaiter().GetResult();
                Task.Run(() => db.SaveChangesAsync()).GetAwaiter().GetResult();
            }
            else
            {
                db.SaveChangesAsync().GetAwaiter().GetResult();
            }

            completed = Completion.Of(Transaction.Current!);

            // Another request caches Ada's committed row again.
            evictedInScope = adaRead.WasEvictedForAnotherCaller().GetAwaiter().GetResult();
            if (complete)
            {
                scope.Complete();
            }
        });

        await completed;
        evictedInScope
            .Should()
            .BeTrue("Sluice can't tell the save is in the scope, so it publishes at once");
        (await reader.NameOf(ada))
            .Should()
            .Be(complete ? "Ada (edited)" : "Ada", "the save was in the scope");
        (await adaRead.WasEvicted())
            .Should()
            .BeTrue("the scope's completion published the save again");
        node.Logs.GetSnapshot()
            .Should()
            .ContainSingle(record =>
                record.Category == typeof(SluiceInterceptor).FullName
                && record.Level == LogLevel.Warning
                && record.Id.Id == 5
            );
    }

    // Runs body on a new thread, as code holding a TransactionScope that doesn't flow into async code must.
    internal static Task OnAThreadOfItsOwn(Action body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                body();
                done.SetResult();
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        }).Start();
        return done.Task;
    }

    [Fact]
    public async Task A_completing_TransactionScope_evicts_without_going_through_async_code()
    {
        // Any async call into the distributed cache throws, and FusionCache logs it as a warning.
        await using var node = new SluiceNode(cache =>
            cache
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(new SyncOnlyDistributedCache())
        );
        await using var reader = postgres.CreateDb(enlist: false);
        var (ada, _) = await reader.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);
        Task completed;

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node);
            await db.Rename(ada, "Ada Lovelace");
            completed = Completion.Of(Transaction.Current!);
            node.Logs.Clear();
            scope.Complete();
        }

        await completed;
        node.Logs.GetSnapshot()
            .Should()
            .NotContain(record => record.Level >= LogLevel.Warning, "completion published sync");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }
}
