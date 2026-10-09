using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sluice.EntityFrameworkCore.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// When a save inside an enlisted or ambient (<c>TransactionScope</c>) transaction publishes. Reads run on a context
/// that doesn't enlist, so they run inside a scope too.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AmbientTransactionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ShopDb _reader = null!;

    public Task InitializeAsync()
    {
        _reader = postgres.CreateDb(enlist: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _reader.DisposeAsync().AsTask();

    private async Task<(int Ada, int Bob)> AddCustomers()
    {
        var ada = new Customer { Name = "Ada" };
        var bob = new Customer { Name = "Bob" };
        _reader.Customers.AddRange(ada, bob);
        await _reader.SaveChangesAsync();
        _reader.ChangeTracker.Clear();
        return (ada.Id, bob.Id);
    }

    private Task<CachedRead> ReadCustomer(SluiceNode node, int id) =>
        CachedRead.Of(
            node.Cache,
            (read, ct) => read.Entity(_reader.Customers, id).SingleOrDefaultAsync(ct)
        );

    private static async Task Rename(ShopDb db, int id, string name)
    {
        (await db.Customers.SingleAsync(c => c.Id == id)).Name = name;
        await db.SaveChangesAsync();
    }

    private Task<string> NameOf(int id) =>
        _reader.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).SingleAsync();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inside_a_TransactionScope_a_save_is_evicted_only_once_the_scope_completes_whatever_its_outcome(
        bool complete
    )
    {
        await using var node = new SluiceNode();
        var (ada, _) = await AddCustomers();
        var adaRead = await ReadCustomer(node, ada);
        Task completed;

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node);
            await Rename(db, ada, "Ada Lovelace");
            completed = Completion.Of(Transaction.Current!);
            (await adaRead.WasEvicted()).Should().BeFalse("the scope hasn't completed");

            if (complete)
            {
                scope.Complete();
            }
        }

        await completed;
        (await NameOf(ada)).Should().Be(complete ? "Ada Lovelace" : "Ada");
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
        var (ada, _) = await AddCustomers();
        var adaRead = await ReadCustomer(node, ada);
        using var transaction = new CommittableTransaction();
        await using var db = postgres.CreateDb(node);
        await db.Database.OpenConnectionAsync();
        db.Database.EnlistTransaction(transaction);

        await Rename(db, ada, "Ada Lovelace");
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
        (await NameOf(ada)).Should().Be(commit ? "Ada Lovelace" : "Ada");
        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Contexts_saving_inside_one_TransactionScope_are_all_evicted_when_it_completes()
    {
        await using var node = new SluiceNode();
        var (ada, bob) = await AddCustomers();
        var adaRead = await ReadCustomer(node, ada);
        var bobRead = await ReadCustomer(node, bob);
        Task completed;

        // One connection, so the scope needs no distributed transaction.
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await connection.OpenAsync();

            ShopDb OnTheConnection()
            {
                var options = new DbContextOptionsBuilder<ShopDb>();
                options.UseNpgsql(connection).UseSluice(node.Services);
                return new(options.Options);
            }

            await using var first = OnTheConnection();
            await using var second = OnTheConnection();
            await Rename(first, ada, "Ada Lovelace");
            await Rename(second, bob, "Bob Babbage");
            completed = Completion.Of(Transaction.Current!);
            (await adaRead.WasEvicted()).Should().BeFalse("the scope hasn't completed");
            (await bobRead.WasEvicted()).Should().BeFalse("the scope hasn't completed");

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
        var (ada, _) = await AddCustomers();
        var adaRead = await ReadCustomer(node, ada);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node, enlist: false);
            await Rename(db, ada, "Ada Lovelace");

            (await adaRead.WasEvicted()).Should().BeTrue("EF committed the save at once");
        }

        (await NameOf(ada)).Should().Be("Ada Lovelace", "the aborted scope didn't hold the save");
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
        var (ada, _) = await AddCustomers();
        var adaRead = await ReadCustomer(node, ada);
        Task completed;

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var db = postgres.CreateDb(node);
            await Rename(db, ada, "Ada Lovelace");
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
