using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// Inside a compute, EF reads go through the read helpers, and a helper's query reads only its own rows, so every row
/// a cached value was built from is covered by a recorded dependency.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StrictReadTests(PostgresFixture postgres)
{
    private const string NotFromHelper = "*wasn't built by a Sluice read helper*";

    private static readonly Query<string, int> _caught = new("ef-strict-caught");

    private static readonly Func<ShopDb, int, Task<int>> _ordersOf = EF.CompileAsyncQuery(
        (ShopDb db, int customerId) => db.Orders.Count(o => o.CustomerId == customerId)
    );

    // Queries not built by a read helper, one per command path EF has, sync and async.
    private static readonly Dictionary<string, Func<ShopDb, CancellationToken, Task>> _plain = new()
    {
        ["query"] = (db, _) => Task.FromResult(db.Orders.ToList()),
        ["query async"] = (db, ct) => db.Orders.ToListAsync(ct),
        ["scalar"] = (db, _) => Task.FromResult(SelectOne(db).ExecuteScalar(Parameters(db))),
        ["scalar async"] = (db, ct) => SelectOne(db).ExecuteScalarAsync(Parameters(db), ct),
        ["non-query"] = (db, _) => Task.FromResult(db.Database.ExecuteSqlRaw("select 1")),
        ["non-query async"] = (db, ct) => db.Database.ExecuteSqlRawAsync("select 1", ct),
        ["FromSql"] = (db, ct) =>
            db.Orders.FromSqlRaw("""select * from "Orders" """).ToListAsync(ct),
        ["compiled query"] = (db, _) => _ordersOf(db, 1),
        ["save"] = (db, ct) =>
        {
            db.Customers.Add(new Customer { Name = "Cy" });
            return db.SaveChangesAsync(ct);
        },
    };

    // Helper queries that read beyond their own rows, or track, each with the message it must fail with.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, SluiceReads, CancellationToken, Task> Read, string Message)
    > _beyondOwnRows = new()
    {
        ["navigation in a projection"] = (
            (db, r, ct) => r.All(db.Orders).Select(o => o.Customer!.Name).ToListAsync(ct),
            "*navigation Order.Customer*"
        ),
        ["navigation in a filter"] = (
            (db, r, ct) =>
                r.Children(db.Orders, o => o.CustomerId, 1)
                    .Where(o => o.Customer!.Name == "Ada")
                    .ToListAsync(ct),
            "*navigation Order.Customer*"
        ),
        ["navigation through EF.Property"] = (
            (db, r, ct) =>
                r.All(db.Orders)
                    .Select(o => EF.Property<Customer>(o, "Customer").Name)
                    .ToListAsync(ct),
            "*navigation Order.Customer*"
        ),
        ["navigation through an interface"] = (
            (db, r, ct) => CustomerNames(r.All(db.Orders)).ToListAsync(ct),
            "*navigation Order.Customer*"
        ),
        ["many-to-many navigation aggregate"] = (
            (db, r, ct) => r.Entity(db.Customers, 1).Select(c => c.Coupons.Count).ToListAsync(ct),
            "*navigation Customer.Coupons*"
        ),
        ["Include"] = (
            (db, r, ct) => r.All(db.Orders).Include(o => o.Customer).ToListAsync(ct),
            "*Include*"
        ),
        ["Include by name"] = (
            (db, r, ct) => r.All(db.Orders).Include("Customer").ToListAsync(ct),
            "*Include*"
        ),
        ["subquery"] = (
            (db, r, ct) =>
                r.All(db.Orders)
                    .Where(o =>
                        db.Notes.Any(n => EF.Property<int>(n, "CustomerId") == o.CustomerId)
                    )
                    .ToListAsync(ct),
            "*also reads Note*"
        ),
        ["join"] = (
            (db, r, ct) =>
                r.All(db.Orders)
                    .Join(db.Customers, o => o.CustomerId, c => c.Id, (o, c) => c.Name)
                    .ToListAsync(ct),
            "*also reads Customer*"
        ),
        ["union with another set"] = (
            (db, r, ct) =>
                r.All(db.Orders)
                    .Select(o => o.Id)
                    .Union(db.Customers.Select(c => c.Id))
                    .ToListAsync(ct),
            "*also reads Customer*"
        ),
        ["FromSql in a subquery"] = (
            (db, r, ct) =>
                r.All(db.Orders)
                    .Where(o =>
                        db.Orders.FromSqlRaw("""select * from "Orders" """).Any(x => x.Id == o.Id)
                    )
                    .ToListAsync(ct),
            "*also reads Order*"
        ),
        ["AsTracking"] = (
            (db, r, ct) => r.All(db.Orders).AsTracking().ToListAsync(ct),
            "*tracking*"
        ),
        ["AsTracking(TrackAll)"] = (
            (db, r, ct) =>
                r.All(db.Orders).AsTracking(QueryTrackingBehavior.TrackAll).ToListAsync(ct),
            "*tracking*"
        ),
    };

    // Generic code reaches the navigation through the interface its type parameter is constrained to.
    private static IQueryable<string> CustomerNames<T>(IQueryable<T> rows)
        where T : IHasCustomer => rows.Select(row => row.Customer!.Name);

    // EF's own scalar path, which sequence-based key generation uses.
    private static IRelationalCommand SelectOne(ShopDb db) =>
        db.GetService<IRawSqlCommandBuilder>().Build("select 1");

    private static RelationalCommandParameterObject Parameters(ShopDb db) =>
        new(
            db.GetService<IRelationalConnection>(),
            null,
            null,
            db,
            db.GetService<IRelationalCommandDiagnosticsLogger>()
        );

    [Theory]
    [InlineData("query")]
    [InlineData("query async")]
    [InlineData("scalar")]
    [InlineData("scalar async")]
    [InlineData("non-query")]
    [InlineData("non-query async")]
    [InlineData("FromSql")]
    [InlineData("compiled query")]
    [InlineData("save")]
    public async Task A_query_not_built_by_a_read_helper_throws_inside_a_compute_and_runs_outside_one(
        string kind
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var query = _plain[kind];

        var inside = () => InCompute.Run((_, ct) => query(db, ct));

        await inside.Should().ThrowAsync<InvalidOperationException>().WithMessage(NotFromHelper);
        await query.Invoking(q => q(db, CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Lazy_and_explicit_loading_throw_inside_a_compute_and_run_outside_one()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var reviewId = await AddReview(db);
        var review = await db.Reviews.SingleAsync(r => r.Id == reviewId);

        var lazy = () => InCompute.Run((_, _) => Task.FromResult(review.Customer));
        var explicitly = () =>
            InCompute.Run((_, ct) => db.Entry(review).Reference(r => r.Customer).LoadAsync(ct));

        await lazy.Should().ThrowAsync<InvalidOperationException>().WithMessage(NotFromHelper);
        await explicitly
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(NotFromHelper);
        review.Customer!.Name.Should().Be("Ada", "outside a compute it loads lazily");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_compute_that_catches_a_strict_read_violation_caches_nothing_and_its_caller_still_gets_it(
        bool shape
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var runs = 0;

        async Task<int> Compute(SluiceReads read, CancellationToken ct)
        {
            runs++;
            await read.All(db.Customers).ToListAsync(ct);
            try
            {
                await (
                    shape
                        ? read.All(db.Orders).Include(o => o.Customer).ToListAsync(ct)
                        : db.Orders.ToListAsync(ct)
                );
            }
            catch (InvalidOperationException)
            {
                // A compute that falls back when a read fails.
            }

            return runs;
        }

        var call = () => node.Cache.GetOrSetAsync(_caught, "key", Compute).AsTask();

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(shape ? "*Include*" : NotFromHelper);
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached");
    }

    [Theory]
    [InlineData("navigation in a projection")]
    [InlineData("navigation in a filter")]
    [InlineData("navigation through EF.Property")]
    [InlineData("navigation through an interface")]
    [InlineData("many-to-many navigation aggregate")]
    [InlineData("Include")]
    [InlineData("Include by name")]
    [InlineData("subquery")]
    [InlineData("join")]
    [InlineData("union with another set")]
    [InlineData("FromSql in a subquery")]
    [InlineData("AsTracking")]
    [InlineData("AsTracking(TrackAll)")]
    public async Task A_read_helper_query_that_reads_beyond_its_own_rows_or_tracks_throws_inside_a_compute(
        string shape
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (read, message) = _beyondOwnRows[shape];

        var inside = () => InCompute.Run((r, ct) => read(db, r, ct));

        await inside.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public async Task A_read_helper_query_may_read_owned_values_and_shape_its_own_rows()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var parcel = new Parcel
        {
            CustomerId = ada,
            Address = new Address
            {
                Street = "1 Loop Rd",
                Contact = new Contact { Phone = "555" },
            },
            Stops = [new Stop { Place = "Depot" }, new Stop { Place = "Door" }],
        };
        db.Parcels.Add(parcel);
        await db.SaveChangesAsync();
        string[] read = [];

        await InCompute.Run(
            async (r, ct) =>
            {
                var whole = await r.Entity(db.Parcels, parcel.Id).SingleAsync(ct);
                var owned = await r.Children(db.Parcels, p => p.CustomerId, ada)
                    .Where(p => p.Address.Street != "")
                    .OrderBy(p => p.Id)
                    .Select(p => $"{p.Address.Contact.Phone}/{p.Stops.Count}")
                    .SingleAsync(ct);
                var resolved = await r.All(db.Parcels)
                    .AsNoTrackingWithIdentityResolution()
                    .Where(p => p.Id == parcel.Id)
                    .Select(p => p.Stops.OrderBy(s => s.Place).First().Place)
                    .SingleAsync(ct);
                var untracked = await r.Entity(db.Parcels, parcel.Id)
                    .AsTracking(QueryTrackingBehavior.NoTracking)
                    .Skip(0)
                    .Take(1)
                    .CountAsync(ct);
                var split = await r.Entity(db.Parcels, parcel.Id).AsSplitQuery().SingleAsync(ct);
                read =
                [
                    whole.Address.Street,
                    owned,
                    resolved,
                    $"{untracked}",
                    $"{split.Stops.Count}",
                ];
            }
        );

        read.Should().Equal("1 Loop Rd", "555/2", "Depot", "1", "2");
    }

    [Fact]
    public async Task A_read_helper_does_not_load_auto_included_navigations()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var reviewId = await AddReview(db);
        Review? read = null;

        await InCompute.Run(
            async (r, ct) => read = await r.Entity(db.Reviews, reviewId).SingleAsync(ct)
        );

        read!.Order.Should().BeNull("the helper reads only the review's own row");
        (await db.Reviews.AsNoTracking().SingleAsync(r => r.Id == reviewId))
            .Order.Should()
            .NotBeNull("a plain query does auto-include it");
    }

    [Fact]
    public async Task The_shape_check_runs_outside_a_compute_too_and_a_rejected_query_is_never_cached()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        IQueryable<Order>? escaped = null;

        // The same shape without the marker compiles and is cached; the marked query must not share its entry.
        await db.Orders.AsNoTracking().IgnoreAutoIncludes().Include(o => o.Customer).ToListAsync();
        var inside = () =>
            InCompute.Run(
                (r, ct) => (escaped = r.All(db.Orders)).Include(o => o.Customer).ToListAsync(ct)
            );
        var outside = () => escaped!.Include(o => o.Customer).ToListAsync();

        await inside.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Include*");
        await outside.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Include*");
        await outside
            .Should()
            .ThrowAsync<InvalidOperationException>(
                "a rejected query isn't cached, so the check runs again"
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_helper_query_built_outside_a_transaction_throws_when_it_runs_inside_one(
        bool scope
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();

        var read = () =>
            InCompute.Run(
                async (r, ct) =>
                {
                    var query = r.Entity(db.Customers, ada);
                    if (scope)
                    {
                        using var transaction = new TransactionScope(
                            TransactionScopeAsyncFlowOption.Enabled
                        );
                        await query.SingleAsync(ct);
                    }
                    else
                    {
                        await using var transaction = await db.Database.BeginTransactionAsync(ct);
                        await query.SingleAsync(ct);
                    }
                }
            );

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transaction*");
    }

    // A review of Ada's order, saved and then forgotten, so it's loaded afresh.
    private static async Task<int> AddReview(ShopDb db)
    {
        var (ada, _) = await db.AddCustomers();
        var order = new Order { CustomerId = ada, Item = "Tea" };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var review = new Review { CustomerId = ada, OrderId = order.Id };
        db.Reviews.Add(review);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return review.Id;
    }
}
