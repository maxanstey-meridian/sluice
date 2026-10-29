using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// SLUICE-EF-028: a write started inside <see cref="SluiceFlow.InvalidateNow"/> publishes hard, evicting the stale
/// copies of queries with <c>MaxStaleness</c> too, wherever and whenever it publishes; one started outside it publishes
/// soft. Each read is cached twice, strictly and with a <c>MaxStaleness</c> no test outlives, so a test sees both that
/// the write published and how: a soft publish evicts only the strict one.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HardWriteTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_save_publishes_hard_inside_InvalidateNow_and_soft_outside_it(
        bool inside,
        bool sync
    )
    {
        await using var scene = await Scene.Of(postgres);

        using (inside ? SluiceFlow.InvalidateNow() : null)
        {
            scene.Change();
            await Save(scene.Db, sync);
        }

        await scene.ShouldBeEvicted(hard: inside);
    }

    // The flag is read when the save runs, so the scope's state at the commit doesn't count.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_save_in_an_EF_transaction_publishes_as_the_flag_was_when_it_saved_not_when_it_committed(
        bool savedInside
    )
    {
        await using var scene = await Scene.Of(postgres);
        await using var transaction = await scene.Db.Database.BeginTransactionAsync();

        using (savedInside ? SluiceFlow.InvalidateNow() : null)
        {
            scene.Change();
            await scene.Db.SaveChangesAsync();
        }

        using (savedInside ? null : SluiceFlow.InvalidateNow())
        {
            await transaction.CommitAsync();
        }

        await scene.ShouldBeEvicted(hard: savedInside);
    }

    // A local transaction raises TransactionCompleted on the thread disposing the scope, which sees the flow's flag, so
    // the scope is disposed outside the flag when the save was inside it, and inside it when the save wasn't.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_save_in_a_TransactionScope_publishes_as_the_flag_was_when_it_saved_not_when_the_scope_completed(
        bool savedInside
    )
    {
        await using var scene = await Scene.Of(postgres);
        Task completed;

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            using (savedInside ? SluiceFlow.InvalidateNow() : null)
            {
                scene.Change();
                await scene.Db.SaveChangesAsync();
            }

            completed = Completion.Of(Transaction.Current!);
            scope.Complete();
            using (savedInside ? null : SluiceFlow.InvalidateNow())
            {
                scope.Dispose();
            }
        }

        await completed;
        await scene.ShouldBeEvicted(hard: savedInside);
    }

    // Each kind takes its own branch of what a bulk write publishes: the whole type, updated or deleted, or the pinned
    // keys. In a transaction, it commits in the scope's opposite state, so the flag counts when the statement ran, not
    // when it committed.
    [Theory]
    [InlineData("ExecuteUpdate", false, true)]
    [InlineData("ExecuteDelete", false, true)]
    [InlineData("keyed ExecuteUpdateAsync", false, true)]
    [InlineData("keyed ExecuteDeleteAsync", false, true)]
    [InlineData("ExecuteUpdate", true, true)]
    [InlineData("ExecuteDelete", true, true)]
    [InlineData("keyed ExecuteUpdateAsync", true, true)]
    [InlineData("keyed ExecuteDeleteAsync", true, true)]
    [InlineData("ExecuteUpdate", false, false)]
    [InlineData("keyed ExecuteDeleteAsync", true, false)]
    public async Task A_bulk_write_publishes_hard_inside_InvalidateNow_and_soft_outside_it(
        string kind,
        bool inTransaction,
        bool inside
    )
    {
        await using var scene = await Scene.Of(postgres);
        var orders = scene.Db.Orders;
        var id = scene.Order.Id;
        await using var transaction = inTransaction
            ? await scene.Db.Database.BeginTransactionAsync()
            : null;

        using (inside ? SluiceFlow.InvalidateNow() : null)
        {
            switch (kind)
            {
                case "ExecuteUpdate":
                    orders
                        .Where(o => o.Id == id)
                        .ExecuteUpdate(set => set.SetProperty(o => o.Item, "Cake"));
                    break;
                case "ExecuteDelete":
                    orders.Where(o => o.Id == id).ExecuteDelete();
                    break;
                case "keyed ExecuteUpdateAsync":
                    await orders
                        .ForKey(id)
                        .ExecuteUpdateAsync(set => set.SetProperty(o => o.Item, "Cake"));
                    break;
                default:
                    await orders.ForKey(id).ExecuteDeleteAsync();
                    break;
            }
        }

        if (transaction is not null)
        {
            using (inside ? null : SluiceFlow.InvalidateNow())
            {
                await transaction.CommitAsync();
            }
        }

        await scene.ShouldBeEvicted(hard: inside, only: scene.OrderRead);
    }

    // An interceptor after Sluice swaps the command EF initialised for another, so Sluice noted nothing on the one that
    // runs, and reads the flag as the statement ends, still on the writing flow.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bulk_write_Sluice_did_not_see_initialised_publishes_hard_inside_InvalidateNow_and_soft_outside_it(
        bool inside
    )
    {
        await using var scene = await Scene.Of(postgres, after: [new ReplaceBulkCommand()]);
        var id = scene.Order.Id;

        using (inside ? SluiceFlow.InvalidateNow() : null)
        {
            await scene
                .Db.Orders.Where(o => o.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.Item, "Cake"));
        }

        await scene.ShouldBeEvicted(hard: inside, only: scene.OrderRead);
    }

    // What such a save wrote is unknown, so it publishes every type whole: hard when it started inside the scope, soft
    // when it started outside it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_save_Sluice_saw_no_command_of_publishes_every_type_hard_inside_InvalidateNow_and_soft_outside_it(
        bool inside
    )
    {
        await using var scene = await Scene.Of(postgres, after: [new SuppressSave(rows: 1)]);

        using (inside ? SluiceFlow.InvalidateNow() : null)
        {
            scene.Change();
            await scene.Db.SaveChangesAsync();
        }

        await scene.ShouldBeEvicted(hard: inside);
    }

    // The interceptor saves again from SavedChanges, before Sluice sees the first save complete, so the second save
    // takes the first as a leftover: it publishes it at once, and hands it to the live transaction, which publishes it
    // again at the commit, after the scope is disposed.
    [Fact]
    public async Task A_leftover_save_keeps_its_flag_when_the_save_after_it_publishes_it()
    {
        var saveAgain = new SaveAgain(whileSaving: false);
        await using var scene = await Scene.Of(postgres, before: [saveAgain]);
        var bob = await scene.Db.Customers.SingleAsync(c => c.Id == scene.Bob);
        await using var transaction = await scene.Db.Database.BeginTransactionAsync();

        using (SluiceFlow.InvalidateNow())
        {
            scene.Change();
            saveAgain.Once(() => bob.Name = "Bob Babbage");
            await scene.Db.SaveChangesAsync();
        }

        await scene.ShouldBeEvicted(hard: true, because: "the leftover published at once");
        await transaction.CommitAsync();
        await scene.ShouldBeEvicted(hard: true, because: "the commit publishes the leftover again");
    }

    // A rollback to a savepoint keeps what it undid, to publish when the transaction ends, since it may have read it.
    [Fact]
    public async Task A_save_rolled_back_to_a_savepoint_still_publishes_hard_when_its_transaction_commits()
    {
        await using var scene = await Scene.Of(postgres);
        await using var transaction = await scene.Db.Database.BeginTransactionAsync();
        await transaction.CreateSavepointAsync("before");

        using (SluiceFlow.InvalidateNow())
        {
            scene.Change();
            await scene.Db.SaveChangesAsync();
        }

        await transaction.RollbackToSavepointAsync("before");
        await transaction.CommitAsync();

        await scene.ShouldBeEvicted(hard: true);
    }

    private static async Task Save(ShopDb db, bool sync)
    {
        if (sync)
        {
            db.SaveChanges();
        }
        else
        {
            await db.SaveChangesAsync();
        }
    }

    // A node, a context on it, and Ada's order, read four ways before the test writes: by key, by a plain query of
    // orders, as one of Ada's orders, and as the page a unique read of a fresh slug found missing.
    private sealed class Scene : IAsyncDisposable
    {
        private readonly SluiceNode _node;
        private readonly string _slug;
        private readonly Twice[] _reads;

        private Scene(
            SluiceNode node,
            ShopDb db,
            int bob,
            Order order,
            string slug,
            Twice orderRead,
            Twice[] reads
        )
        {
            _node = node;
            Db = db;
            Bob = bob;
            Order = order;
            _slug = slug;
            OrderRead = orderRead;
            _reads = reads;
        }

        public ShopDb Db { get; }

        public int Bob { get; }

        /// <summary>Ada's order, tracked by <see cref="Db"/>.</summary>
        public Order Order { get; }

        /// <summary>The order read by key.</summary>
        public Twice OrderRead { get; }

        public static async Task<Scene> Of(
            PostgresFixture postgres,
            IInterceptor[]? before = null,
            IInterceptor[]? after = null
        )
        {
            var node = new SluiceNode();
            var db = postgres.CreateDb(node, before: before, after: after);
            var (ada, bob) = await db.AddCustomers();
            var order = new Order { CustomerId = ada, Item = "Tea" };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            var slug = $"hard-{Guid.NewGuid():N}";
            var cache = node.Cache;

            var orderRead = await Twice.Of(
                cache,
                "the order read by key",
                (read, ct) =>
                    read.Entity(read.Db<ShopDb>().Orders, order.Id).SingleOrDefaultAsync(ct)
            );
            Twice[] reads =
            [
                orderRead,
                await Twice.Of(
                    cache,
                    "a plain query of orders",
                    (read, ct) =>
                        read.Db<ShopDb>().Orders.Where(o => o.Item == "Tea").ToListAsync(ct)
                ),
                await Twice.Of(
                    cache,
                    "Ada's orders",
                    (read, ct) =>
                        read.Children(read.Db<ShopDb>().Orders, o => o.CustomerId, ada)
                            .ToListAsync(ct)
                ),
                await Twice.Of(
                    cache,
                    "the page a unique read found missing",
                    async (read, ct) =>
                        await (
                            await read.EntityAsync(read.Db<ShopDb>().Pages, p => p.Slug, slug, ct)
                        ).SingleOrDefaultAsync(ct)
                ),
            ];
            return new(node, db, bob, order, slug, orderRead, reads);
        }

        /// <summary>Changes Ada's order, and adds a page with the slug the unique read found missing.</summary>
        public void Change()
        {
            Order.Item = "Cake";
            Db.Pages.Add(
                new Page
                {
                    Slug = _slug,
                    Title = "Erased",
                    Section = $"hard-{Guid.NewGuid():N}",
                }
            );
        }

        /// <summary>
        /// Asserts the write published each read, or <paramref name="only"/> that one: the strict one evicted, and the
        /// stale copy evicted only when <paramref name="hard"/>.
        /// </summary>
        public async Task ShouldBeEvicted(bool hard, string because = "", Twice? only = null)
        {
            foreach (var read in only is null ? _reads : [only])
            {
                var (strict, stale) = await read.Evicted();
                strict.Should().BeTrue($"the write published {read.Name} {because}");
                stale
                    .Should()
                    .Be(
                        hard,
                        hard
                            ? $"a hard publish evicts the stale copy of {read.Name} {because}"
                            : $"a soft publish leaves the stale copy of {read.Name} {because}"
                    );
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _node.DisposeAsync();
        }
    }

    // One read cached by a strict query and by a staleness one, each under a key of its own.
    private sealed class Twice
    {
        private static readonly Query<Guid, int> _staleness = new("ef-hard-write")
        {
            MaxStaleness = TimeSpan.FromMinutes(1),
        };

        private readonly CachedRead _strict;
        private readonly CachedRead _stale;

        private Twice(string name, CachedRead strict, CachedRead stale)
        {
            Name = name;
            _strict = strict;
            _stale = stale;
        }

        public string Name { get; }

        public static async Task<Twice> Of(
            SluiceCache cache,
            string name,
            Func<SluiceReads, CancellationToken, Task> read
        ) =>
            new(
                name,
                await CachedRead.Of(cache, read),
                await CachedRead.Of(cache, read, _staleness)
            );

        /// <summary>Reads both again: whether each had been evicted and was recomputed.</summary>
        public async Task<(bool Strict, bool Stale)> Evicted() =>
            (await _strict.WasEvicted(), await _stale.WasEvicted());
    }
}
