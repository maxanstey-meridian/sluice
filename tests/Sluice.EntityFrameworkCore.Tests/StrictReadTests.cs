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
    private const string Write = "*Writes can't run inside a compute*";
    private const string Rename = """update "Customers" set "Name" = 'Cy' where "Id" = -1""";

    private static readonly Query<string, int> _caught = new("ef-strict-caught");

    private static readonly Source<int> _totals = new("ef-customer-totals");

    private static readonly Func<ShopDb, int, Task<int>> _ordersOf = EF.CompileAsyncQuery(
        (ShopDb db, int customerId) => db.Orders.Count(o => o.CustomerId == customerId)
    );

    // Commands not built by a read helper, one per command path EF has, sync and async, each with the message it must
    // fail with: writes are told so.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, CancellationToken, Task> Run, string Message)
    > _plain = new()
    {
        ["query"] = ((db, _) => Task.FromResult(db.Orders.ToList()), NotFromHelper),
        ["query async"] = ((db, ct) => db.Orders.ToListAsync(ct), NotFromHelper),
        ["scalar"] = (
            (db, _) => Task.FromResult(SelectOne(db).ExecuteScalar(Parameters(db))),
            NotFromHelper
        ),
        ["scalar async"] = (
            (db, ct) => SelectOne(db).ExecuteScalarAsync(Parameters(db), ct),
            NotFromHelper
        ),
        ["non-query"] = ((db, _) => Task.FromResult(db.Database.ExecuteSqlRaw(Rename)), Write),
        ["non-query async"] = ((db, ct) => db.Database.ExecuteSqlRawAsync(Rename, ct), Write),
        ["FromSql"] = (
            (db, ct) => db.Orders.FromSqlRaw("""select * from "Orders" """).ToListAsync(ct),
            NotFromHelper
        ),
        ["SqlQuery"] = (
            (db, ct) => db.Database.SqlQueryRaw<int>("""select 1 as "Value" """).ToListAsync(ct),
            NotFromHelper
        ),
        ["compiled query"] = ((db, _) => _ordersOf(db, 1), NotFromHelper),
        ["save"] = (
            (db, ct) =>
            {
                db.Customers.Add(new Customer { Name = "Cy" });
                return db.SaveChangesAsync(ct);
            },
            Write
        ),
        ["ExecuteUpdate"] = (
            (db, ct) =>
                db
                    .Customers.Where(c => c.Id == -1)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Cy"), ct),
            Write
        ),
        ["ExecuteDelete"] = (
            (db, ct) => db.Coupons.Where(c => c.Id == -1).ExecuteDeleteAsync(ct),
            Write
        ),
    };

    // Helper queries that read beyond their own rows, track, write or return lazily loading entities, each with the
    // message it must fail with.
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
        ["derived type's navigation through EF.Property"] = (
            (db, r, ct) =>
                r.All(db.Animals)
                    .Select(a => EF.Property<Customer>(a, "Owner").Name)
                    .ToListAsync(ct),
            "*navigation Dog.Owner*"
        ),
        ["navigation through a property bag indexer"] = (
            (db, r, ct) => r.All(db.CustomerCoupons).Select(cc => cc["Coupon"]).ToListAsync(ct),
            "*navigation CustomerCoupons*.Coupon*"
        ),
        ["navigation through an interface"] = (
            (db, r, ct) => CustomerNames(r.All(db.Orders)).ToListAsync(ct),
            "*navigation Order.Customer*"
        ),
        ["navigation through Cast to an interface"] = (
            (db, r, ct) =>
                r.All(db.Orders).Cast<IHasCustomer>().Select(o => o.Customer!.Name).ToListAsync(ct),
            "*navigation Order.Customer*"
        ),
        ["navigation through a cast to a derived type"] = (
            (db, r, ct) => r.All(db.Animals).Select(a => ((Dog)a).Owner!.Name).ToListAsync(ct),
            "*navigation Dog.Owner*"
        ),
        ["navigation through as to a derived type"] = (
            (db, r, ct) => r.All(db.Animals).Select(a => (a as Dog)!.Owner!.Name).ToListAsync(ct),
            "*navigation Dog.Owner*"
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
        ["SqlQuery in a subquery"] = (
            (db, r, ct) =>
                r.All(db.Orders)
                    .Where(o =>
                        db.Database.SqlQueryRaw<int>("""select 1 as "Value" """)
                            .Contains(o.CustomerId)
                    )
                    .ToListAsync(ct),
            "*also reads SqlQuery<Int32>*"
        ),
        ["database function of the app's"] = (
            (db, r, ct) =>
                r.All(db.Orders).Select(o => ShopDb.CustomerName(o.CustomerId)).ToListAsync(ct),
            "*database function customer_name*"
        ),
        ["query filter with a navigation"] = (
            (db, r, ct) => r.All(db.Tickets).ToListAsync(ct),
            "*navigation Ticket.Customer*Ticket's query filter*"
        ),
        ["query filter another filter's name doesn't ignore"] = (
            (db, r, ct) => r.All(db.Tickets).IgnoreQueryFilters(["open"]).ToListAsync(ct),
            "*navigation Ticket.Customer*Ticket's query filter*"
        ),
        ["query filter with a subquery"] = (
            (db, r, ct) => r.All(db.Memos).ToListAsync(ct),
            "*also reads Customer*Memo's query filter*"
        ),
        ["query filter reading the context"] = (
            (db, r, ct) => r.All(db.Accounts).ToListAsync(ct),
            "*Account's query filter, which reads the context's state*"
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
        ["ExecuteUpdate"] = (
            (db, r, ct) =>
                r.All(db.Customers)
                    .Where(c => c.Id == -1)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Cy"), ct),
            "*used for ExecuteUpdate*"
        ),
        ["ExecuteDelete"] = (
            (db, r, ct) => r.Entity(db.Coupons, -1).ExecuteDeleteAsync(ct),
            "*used for ExecuteDelete*"
        ),
        ["lazily loading entities"] = (
            (db, r, ct) => r.All(db.Reviews).ToListAsync(ct),
            "*returns Review entities, which load their navigations lazily*"
        ),
        ["a lazily loading entity"] = (
            (db, r, ct) => r.Entity(db.Reviews, -1).SingleOrDefaultAsync(ct),
            "*returns Review entities*"
        ),
        ["entities with a lazy loader property"] = (
            (db, r, ct) => r.All(db.Receipts).ToListAsync(ct),
            "*returns Receipt entities*"
        ),
        ["lazily loading entities in an anonymous type"] = (
            (db, r, ct) => r.All(db.Reviews).Select(x => new { x.Id, Review = x }).ToListAsync(ct),
            "*returns Review entities*"
        ),
        ["entities with a lazy loader delegate"] = (
            (db, r, ct) => r.All(db.Ratings).ToListAsync(ct),
            "*returns Rating entities, which load their navigations lazily*"
        ),
        ["owners of a lazily loading owned value"] = (
            (db, r, ct) => r.All(db.Deliveries).ToListAsync(ct),
            "*returns Delivery entities, whose owned Signature values load their navigations lazily*"
        ),
        ["a lazily loading owned value"] = (
            (db, r, ct) => r.All(db.Deliveries).Select(d => d.Signature).ToListAsync(ct),
            "*returns *Signature entities, which load their navigations lazily*"
        ),
        ["entities holding their context"] = (
            (db, r, ct) => r.All(db.Baskets).ToListAsync(ct),
            "*returns Basket entities, which hold the context that loaded them*"
        ),
    };

    // Strict-read violations a compute catches, each with the message its caller must still get.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, SluiceReads, CancellationToken, Task> Violate, string Message)
    > _caughtViolations = new()
    {
        ["plain query"] = ((db, _, ct) => db.Orders.ToListAsync(ct), NotFromHelper),
        ["read beyond its own rows"] = (
            (db, r, ct) => r.All(db.Orders).Include(o => o.Customer).ToListAsync(ct),
            "*Include*"
        ),
        ["read beyond its own rows in a fused fetch"] = (
            (db, r, ct) =>
                r.From(
                    _totals.All,
                    fetchCt => r.All(db.Orders).Include(o => o.Customer).ToListAsync(fetchCt)
                ),
            "*Include*"
        ),
        ["read in a transaction in a fused fetch"] = (
            (db, r, ct) =>
                r.From(
                    _totals.All,
                    async fetchCt =>
                    {
                        await using var transaction = await db.Database.BeginTransactionAsync(
                            fetchCt
                        );
                        return await r.All(db.Customers).ToListAsync(fetchCt);
                    }
                ),
            ReadGuardTests.InTransaction
        ),
    };

    // Helper queries that read only their own rows, each with what it reads from the rows AddOwnRows adds.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, SluiceReads, OwnRows, CancellationToken, Task<string>> Read, string Expected)
    > _ownRows = new()
    {
        ["an entity with its owned values and JSON column"] = (
            async (db, r, rows, ct) =>
            {
                var parcel = await r.Entity(db.Parcels, rows.Parcel).SingleAsync(ct);
                return $"{parcel.Address.Street}/{parcel.Address.Contact.Phone}/{parcel.Stops.Count}/{parcel.Label.Text}";
            },
            "1 Loop Rd/555/2/Fragile"
        ),
        ["owned values filtered, ordered and projected"] = (
            (db, r, rows, ct) =>
                r.Children(db.Parcels, p => p.CustomerId, rows.Ada)
                    .Where(p => p.Address.Street != "")
                    .OrderBy(p => p.Id)
                    .Select(p => $"{p.Address.Contact.Phone}/{p.Stops.Count}/{p.Label.Text}")
                    .SingleAsync(ct),
            "555/2/Fragile"
        ),
        ["an owned collection under identity resolution"] = (
            (db, r, rows, ct) =>
                r.All(db.Parcels)
                    .AsNoTrackingWithIdentityResolution()
                    .Where(p => p.Id == rows.Parcel)
                    .Select(p => p.Stops.OrderBy(s => s.Place).First().Place)
                    .SingleAsync(ct),
            "Depot"
        ),
        ["AsTracking(NoTracking), paged"] = (
            async (db, r, rows, ct) =>
                $"{await r.Entity(db.Parcels, rows.Parcel)
                    .AsTracking(QueryTrackingBehavior.NoTracking)
                    .Skip(0)
                    .Take(1)
                    .CountAsync(ct)}",
            "1"
        ),
        ["an owned collection in a split query"] = (
            async (db, r, rows, ct) =>
                $"{(await r.Entity(db.Parcels, rows.Parcel).AsSplitQuery().SingleAsync(ct)).Stops.Count}",
            "2"
        ),
        ["a projection of an entity that loads lazily"] = (
            async (db, r, rows, ct) =>
                $"{(await r.Entity(db.Reviews, rows.Review).Select(x => new { x.CustomerId }).SingleAsync(ct)).CustomerId == rows.Ada}",
            "True"
        ),
        ["collections of plain values and a built-in function"] = (
            (db, r, rows, ct) =>
            {
                int[] ids = [rows.Ada];
                return r.All(db.Customers)
                    .Where(c => ids.Contains(c.Id) && new[] { rows.Ada, -1 }.Contains(c.Id))
                    .Select(c => ShopDb.Upper(c.Name))
                    .SingleAsync(ct);
            },
            "ADA"
        ),
    };

    public static TheoryData<string> PlainCommands => new(_plain.Keys);

    public static TheoryData<string> ShapesBeyondOwnRows => new(_beyondOwnRows.Keys);

    public static TheoryData<string> CaughtViolations => new(_caughtViolations.Keys);

    public static TheoryData<string> ShapesOfOwnRows => new(_ownRows.Keys);

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
    [MemberData(nameof(PlainCommands))]
    public async Task A_command_not_built_by_a_read_helper_throws_inside_a_compute_and_runs_outside_one(
        string kind
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (command, message) = _plain[kind];

        var inside = () => CachedRead.Of(node.Cache, (_, ct) => command(db, ct));

        await inside.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await command.Invoking(c => c(db, CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Lazy_and_explicit_loading_throw_inside_a_compute_and_run_outside_one()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var reviewId = await AddReview(db);
        var review = await db.Reviews.SingleAsync(r => r.Id == reviewId);

        var lazy = () => CachedRead.Of(node.Cache, (_, _) => Task.FromResult(review.Customer));
        var explicitly = () =>
            CachedRead.Of(
                node.Cache,
                (_, ct) => db.Entry(review).Reference(r => r.Customer).LoadAsync(ct)
            );

        await lazy.Should().ThrowAsync<InvalidOperationException>().WithMessage(NotFromHelper);
        await explicitly
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(NotFromHelper);
        review.Customer!.Name.Should().Be("Ada", "outside a compute it loads lazily");
    }

    [Theory]
    [MemberData(nameof(CaughtViolations))]
    public async Task A_compute_that_catches_a_strict_read_violation_caches_nothing_and_its_caller_still_gets_it(
        string kind
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (violate, message) = _caughtViolations[kind];
        var runs = 0;

        async Task<int> Compute(SluiceReads read, CancellationToken ct)
        {
            runs++;
            await read.All(db.Customers).ToListAsync(ct);
            try
            {
                await violate(db, read, ct);
            }
            catch (InvalidOperationException)
            {
                // A compute that falls back when a read fails.
            }

            return runs;
        }

        var call = () => node.Cache.GetOrSetAsync(_caught, "key", Compute).AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached");
    }

    [Theory]
    [MemberData(nameof(ShapesBeyondOwnRows))]
    public async Task A_read_helper_query_that_reads_beyond_its_own_rows_tracks_writes_or_returns_lazy_entities_throws_inside_a_compute(
        string shape
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (read, message) = _beyondOwnRows[shape];

        var inside = () => CachedRead.Of(node.Cache, (r, ct) => read(db, r, ct));

        await inside.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Theory]
    [MemberData(nameof(ShapesOfOwnRows))]
    public async Task A_read_helper_query_may_read_owned_values_and_shape_its_own_rows(string shape)
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var rows = await AddOwnRows(db);
        var (read, expected) = _ownRows[shape];
        var value = "";

        await CachedRead.Of(node.Cache, async (r, ct) => value = await read(db, r, rows, ct));

        value.Should().Be(expected);
    }

    [Fact]
    public async Task A_read_helper_query_may_keep_query_filters_on_its_own_columns_or_ignore_them()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        db.Tickets.AddRange(
            new Ticket { CustomerId = ada },
            new Ticket { CustomerId = ada, Closed = true }
        );
        db.Memos.Add(new Memo { CustomerId = ada });
        var account = new Account { TenantId = 7 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        int[] read = [];

        await CachedRead.Of(
            node.Cache,
            async (r, ct) =>
                read = [
                    await r.Children(db.Tickets, t => t.CustomerId, ada)
                        .IgnoreQueryFilters(["customer"])
                        .CountAsync(ct),
                    await r.Children(db.Tickets, t => t.CustomerId, ada)
                        .IgnoreQueryFilters()
                        .CountAsync(ct),
                    await r.All(db.Memos)
                        .IgnoreQueryFilters()
                        .Where(m => m.CustomerId == ada)
                        .CountAsync(ct),
                    await r.Entity(db.Accounts, account.Id)
                        .IgnoreQueryFilters()
                        .Where(a => a.TenantId == 7)
                        .CountAsync(ct),
                ]
        );

        read.Should()
            .Equal([1, 2, 1, 1], "the open filter, on the ticket's own column, still applies");
    }

    [Fact]
    public async Task A_read_helper_does_not_load_auto_included_navigations()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var rex = new Dog { Name = "Rex", OwnerId = ada };
        db.Dogs.Add(rex);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Dog? read = null;

        await CachedRead.Of(
            node.Cache,
            async (r, ct) => read = await r.Entity(db.Dogs, rex.Id).SingleAsync(ct)
        );

        read!.Owner.Should().BeNull("the helper reads only the dog's own row");
        (await db.Dogs.AsNoTracking().SingleAsync(d => d.Id == rex.Id))
            .Owner.Should()
            .NotBeNull("a plain query does auto-include it");
    }

    [Fact]
    public async Task A_read_helper_query_that_reads_beyond_its_own_rows_throws_outside_a_compute_too_and_every_time_it_runs()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        IQueryable<Order>? escaped = null;

        // The same shape with another tag compiles and is cached; the marked query must not share its entry.
        await db
            .Orders.TagWith("other tag")
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Include(o => o.Customer)
            .ToListAsync();
        var inside = () =>
            CachedRead.Of(
                node.Cache,
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
            CachedRead.Of(
                node.Cache,
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

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(ReadGuardTests.InTransaction);
    }

    [Fact]
    public async Task A_query_run_in_a_fused_fetch_is_cached_and_evicted_by_its_declared_dependency()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var totals = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.From(_totals.All, fetchCt => db.CustomerTotals.ToListAsync(fetchCt))
        );

        (await totals.WasEvicted()).Should().BeFalse("the fetch's value was cached");
        await node.Cache.InvalidateAsync(_totals.All);
        (await totals.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_query_after_a_read_From_that_only_records_still_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var call = () =>
            CachedRead.Of(
                node.Cache,
                (read, ct) =>
                {
                    read.From(_totals.All);
                    return db.CustomerTotals.ToListAsync(ct);
                }
            );

        await call.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"{NotFromHelper}(SELECT *", "the message names the command");
    }

    [Fact]
    public async Task While_fused_fetches_run_side_by_side_each_runs_plain_queries_and_the_compute_s_own_flow_does_not()
    {
        await using var node = new SluiceNode();
        await using var first = postgres.CreateDb(node);
        await using var second = postgres.CreateDb(node);
        await using var plain = postgres.CreateDb(node);
        int[] fetched = [];

        var call = () =>
            CachedRead.Of(
                node.Cache,
                async (read, ct) =>
                {
                    var release = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    var firstFetch = read.From(
                        _totals.All,
                        async fetchCt =>
                        {
                            await release.Task;
                            return await first.Customers.CountAsync(fetchCt);
                        }
                    );
                    var secondFetch = read.From(
                        _totals.All,
                        async fetchCt =>
                        {
                            await release.Task;
                            return await second.Customers.CountAsync(fetchCt);
                        }
                    );

                    try
                    {
                        // Runs in the compute's own flow while both fetches are in flight.
                        await plain.Customers.CountAsync(ct);
                    }
                    finally
                    {
                        release.SetResult();
                        fetched = await Task.WhenAll(firstFetch, secondFetch);
                    }
                }
            );

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(NotFromHelper);
        fetched.Should().HaveCount(2, "each fetch ran its own plain query");
    }

    [Theory]
    [InlineData("completes synchronously")]
    [InlineData("completes asynchronously")]
    [InlineData("throws synchronously")]
    public async Task A_query_a_fused_fetch_leaves_running_throws_once_the_fetch_has_ended_and_nothing_is_cached(
        string ending
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var runs = 0;

        async Task<int> Compute(SluiceReads read, CancellationToken ct)
        {
            runs++;
            var ended = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Task<int>? left = null;

            async Task<int> CountOnceEnded()
            {
                await ended.Task;
                return await db.Customers.CountAsync();
            }

            Func<CancellationToken, Task<int>> fetch = ending switch
            {
                "completes synchronously" => _ =>
                {
                    left = Task.Run(CountOnceEnded);
                    return Task.FromResult(0);
                },
                "completes asynchronously" => async _ =>
                {
                    left = ended.Task.ContinueWith(
                        _ => db.Customers.Count(),
                        TaskScheduler.Default
                    );
                    await Task.Yield();
                    return 0;
                },
                _ => _ =>
                {
                    left = Task.Run(CountOnceEnded);
                    throw new TimeoutException("The fetch failed.");
                },
            };

            try
            {
                await read.From(_totals.All, fetch);
            }
            catch (TimeoutException)
            {
                // A compute that falls back when its fetch fails.
            }

            ended.SetResult();
            return await left!;
        }

        var call = () => node.Cache.GetOrSetAsync(_caught, ending, Compute).AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(NotFromHelper);
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached");
    }

    [Fact]
    public async Task A_compute_started_from_a_fused_fetch_that_outlived_its_own_compute_is_checked()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CachedRead>? outlived = null;

        // The compute returns without awaiting its fetch, which goes on to run a compute of its own.
        await CachedRead.Of(
            node.Cache,
            (read, _) =>
            {
                outlived = read.From(
                    _totals.All,
                    async _ =>
                    {
                        await release.Task;
                        return await CachedRead.Of(
                            node.Cache,
                            (_, ct) => db.Customers.CountAsync(ct)
                        );
                    }
                );
                return Task.CompletedTask;
            }
        );
        release.SetResult();

        await outlived!
            .Awaiting(task => task)
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(NotFromHelper);
    }

    // Ada and Bob, a parcel of Ada's with its owned values, and a review of Ada, saved and then forgotten.
    private static async Task<OwnRows> AddOwnRows(ShopDb db)
    {
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
            Label = new Label { Text = "Fragile" },
        };
        var review = new Review { CustomerId = ada };
        db.AddRange(parcel, review);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(ada, parcel.Id, review.Id);
    }

    // A review of Ada, saved and then forgotten, so it's loaded afresh.
    private static async Task<int> AddReview(ShopDb db)
    {
        var (ada, _) = await db.AddCustomers();
        var review = new Review { CustomerId = ada };
        db.Reviews.Add(review);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return review.Id;
    }

    private sealed record OwnRows(int Ada, int Parcel, int Review);
}
