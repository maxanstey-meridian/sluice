using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// Inside a compute, every EF query on the compute's own context records each entity type it reads, so a write to any
/// of them evicts the value. What can't be tracked throws there instead, as do writes and queries on other contexts.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TrackedReadTests(PostgresFixture postgres)
{
    private const string NotLinq = "*isn't a LINQ query*";
    private const string Write = "*Writes can't run inside a compute*";
    private const string ForeignContext = "*didn't get from read.Db<TContext>()*";
    private const string Rename = """update "Customers" set "Name" = 'Cy' where "Id" = -1""";

    private static readonly Query<string, int> _caught = new("ef-tracked-caught");
    private static readonly Query<int, int> _wholeTypes = new("ef-tracked-whole-types");
    private static readonly Query<int, int> _helperOnly = new("ef-tracked-helper-only");

    private static readonly Source<int> _totals = new("ef-customer-totals");

    private static readonly Func<ShopDb, int, Task<int>> _ordersOf = EF.CompileAsyncQuery(
        (ShopDb db, int customerId) => db.Orders.Count(o => o.CustomerId == customerId)
    );

    // Commands Sluice can't tell the reads of, one per kind, each with the message it must fail with inside a compute.
    // All of them run outside one.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, CancellationToken, Task> Run, string Message)
    > _untrackable = new()
    {
        ["scalar"] = (
            (db, _) => Task.FromResult(SelectOne(db).ExecuteScalar(Parameters(db))),
            NotLinq
        ),
        ["scalar async"] = (
            (db, ct) => SelectOne(db).ExecuteScalarAsync(Parameters(db), ct),
            NotLinq
        ),
        ["non-query"] = ((db, _) => Task.FromResult(db.Database.ExecuteSqlRaw(Rename)), Write),
        ["non-query async"] = ((db, ct) => db.Database.ExecuteSqlRawAsync(Rename, ct), Write),
        ["FromSql"] = (
            (db, ct) => db.Orders.FromSqlRaw("""select * from "Orders" """).ToListAsync(ct),
            "*Order through FromSql*"
        ),
        ["SqlQuery"] = (
            (db, ct) => db.Database.SqlQueryRaw<int>("""select 1 as "Value" """).ToListAsync(ct),
            "*SqlQuery<Int32>*"
        ),
        ["FromSql in a subquery"] = (
            (db, ct) =>
                db
                    .Orders.Where(o =>
                        db.Orders.FromSqlRaw("""select * from "Orders" """).Any(x => x.Id == o.Id)
                    )
                    .ToListAsync(ct),
            "*Order through FromSql*"
        ),
        ["SqlQuery in a subquery"] = (
            (db, ct) =>
                db
                    .Orders.Where(o =>
                        db.Database.SqlQueryRaw<int>("""select 1 as "Value" """)
                            .Contains(o.CustomerId)
                    )
                    .ToListAsync(ct),
            "*SqlQuery<Int32>*"
        ),
        ["database function of the app's"] = (
            (db, ct) => db.Orders.Select(o => ShopDb.CustomerName(o.CustomerId)).ToListAsync(ct),
            "*database function customer_name*"
        ),
        ["keyless view"] = ((db, ct) => db.CustomerTotals.ToListAsync(ct), "*keyless*"),
        ["table read through a view"] = (
            (db, ct) => db.Suppliers.ToListAsync(ct),
            "*view 'supplier_names'*"
        ),
        ["type mapped to a SQL query"] = (
            (db, ct) => db.TopCustomers.ToListAsync(ct),
            "*SQL query*"
        ),
        ["type mapped to a function"] = (
            (db, ct) => db.RecentOrders.ToListAsync(ct),
            "*function 'recent_orders'*"
        ),
        ["root whose derived type is a view"] = (
            (db, ct) => db.Promotions.ToListAsync(ct),
            "*LivePromotion, which is mapped to the view 'live_promotions'*"
        ),
        ["query filter reading the context"] = (
            (db, ct) => db.Accounts.ToListAsync(ct),
            "*Account's query filter reads the context's state*"
        ),
        ["lazily loading entities"] = (
            (db, ct) => db.Reviews.ToListAsync(ct),
            "*returns Review entities*Review entities load their navigations lazily*"
        ),
        ["entities with a lazy loader property"] = (
            (db, ct) => db.Receipts.ToListAsync(ct),
            "*returns Receipt entities*"
        ),
        ["lazily loading entities in an anonymous type"] = (
            (db, ct) => db.Reviews.Select(x => new { x.Id, Review = x }).ToListAsync(ct),
            "*returns Review entities*"
        ),
        ["lazily loading entities in a nested collection"] = (
            (db, ct) =>
                db
                    .Customers.Select(c => db.Reviews.Where(r => r.CustomerId == c.Id).ToList())
                    .ToListAsync(ct),
            "*returns Review entities*"
        ),
        ["entities with a lazy loader delegate"] = (
            (db, ct) => db.Ratings.ToListAsync(ct),
            "*returns Rating entities*Rating entities load their navigations lazily*"
        ),
        ["owners of a lazily loading owned value"] = (
            (db, ct) => db.Deliveries.ToListAsync(ct),
            "*returns Delivery entities*Signature entities load their navigations lazily*"
        ),
        ["a lazily loading owned value"] = (
            (db, ct) => db.Deliveries.Select(d => d.Signature).ToListAsync(ct),
            "*returns Signature entities*Signature entities load their navigations lazily*"
        ),
        ["entities holding their context"] = (
            (db, ct) => db.Baskets.ToListAsync(ct),
            "*returns Basket entities*Basket entities hold the context that loaded them*"
        ),
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

    // Queries that read beyond their own type, each with a write that must evict it (to a type it reaches) and one
    // that mustn't (to a type it doesn't).
    private static readonly Dictionary<
        string,
        (
            Func<SluiceReads, CancellationToken, Task> Read,
            Func<ShopDb, IEnumerable<Dependency>> Reached,
            Func<ShopDb, IEnumerable<Dependency>> Unread
        )
    > _reaching = new()
    {
        ["navigation in a projection"] = (
            (r, ct) => r.Db<ShopDb>().Orders.Select(o => o.Customer!.Name).ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation in a filter"] = (
            (r, ct) => r.Db<ShopDb>().Orders.Where(o => o.Customer!.Name == "Ada").ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation through EF.Property"] = (
            (r, ct) =>
                r.Db<ShopDb>()
                    .Orders.Select(o => EF.Property<Customer>(o, "Customer").Name)
                    .ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation through a property bag indexer"] = (
            (r, ct) => r.Db<ShopDb>().CustomerCoupons.Select(cc => cc["Coupon"]).ToListAsync(ct),
            db => [db.Coupons.Dependency(-1)],
            db => [db.Orders.Dependency(-1)]
        ),
        ["navigation through an interface"] = (
            (r, ct) => CustomerNames(r.Db<ShopDb>().Orders).ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation through Cast to an interface"] = (
            (r, ct) =>
                r.Db<ShopDb>()
                    .Orders.Cast<IHasCustomer>()
                    .Select(o => o.Customer!.Name)
                    .ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation through a cast to a derived type"] = (
            (r, ct) =>
                r.Db<ShopDb>()
                    .Animals.IgnoreAutoIncludes()
                    .Select(a => ((Dog)a).Owner!.Name)
                    .ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["many-to-many navigation aggregate"] = (
            (r, ct) =>
                r.Entity(r.Db<ShopDb>().Customers, -1).Select(c => c.Coupons.Count).ToListAsync(ct),
            db => [db.Coupons.Dependency(-1)],
            db => [db.Orders.Dependency(-1)]
        ),
        ["many-to-many join rows"] = (
            (r, ct) =>
                r.Entity(r.Db<ShopDb>().Customers, -1).Select(c => c.Coupons.Count).ToListAsync(ct),
            db => db.CustomerCoupons.AllDependencies(),
            db => [db.Orders.Dependency(-1)]
        ),
        ["Include"] = (
            (r, ct) => r.Db<ShopDb>().Orders.Include(o => o.Customer).ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["Include by name"] = (
            (r, ct) => r.Db<ShopDb>().Orders.Include("Customer").ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["auto-included navigation"] = (
            (r, ct) => r.Db<ShopDb>().Dogs.ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["subquery"] = (
            (r, ct) =>
            {
                var db = r.Db<ShopDb>();
                return db
                    .Orders.Where(o =>
                        db.Notes.Any(n => EF.Property<int>(n, "CustomerId") == o.CustomerId)
                    )
                    .ToListAsync(ct);
            },
            db => [db.Notes.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["join"] = (
            (r, ct) =>
            {
                var db = r.Db<ShopDb>();
                return db
                    .Orders.Join(db.Customers, o => o.CustomerId, c => c.Id, (o, c) => c.Name)
                    .ToListAsync(ct);
            },
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["union with another set"] = (
            (r, ct) =>
            {
                var db = r.Db<ShopDb>();
                return db
                    .Orders.Select(o => o.Id)
                    .Union(db.Customers.Select(c => c.Id))
                    .ToListAsync(ct);
            },
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["query filter with a navigation"] = (
            (r, ct) => r.Db<ShopDb>().Tickets.ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["query filter another filter's name doesn't ignore"] = (
            (r, ct) => r.Db<ShopDb>().Tickets.IgnoreQueryFilters(["open"]).ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["query filter with a subquery"] = (
            (r, ct) => r.Db<ShopDb>().Memos.ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["helper query with a navigation"] = (
            (r, ct) =>
                r.Children(r.Db<ShopDb>().Orders, o => o.CustomerId, -1)
                    .Select(o => o.Customer!.Name)
                    .ToListAsync(ct),
            db => [db.Customers.Dependency(-2)],
            db => [db.Orders.Dependency(-2)]
        ),
        ["plain query joined to a helper's query"] = (
            (r, ct) =>
            {
                var db = r.Db<ShopDb>();
                return db
                    .Customers.Join(
                        r.Children(db.Orders, o => o.CustomerId, -1),
                        c => c.Id,
                        o => o.CustomerId,
                        (c, o) => c.Name
                    )
                    .ToListAsync(ct);
            },
            db => [db.Customers.Dependency(-2)],
            db => [db.Orders.Dependency(-2)]
        ),
        ["compiled query"] = (
            (r, ct) => _ordersOf(r.Db<ShopDb>(), -1),
            db => [db.Orders.Dependency(-1)],
            db => [db.Customers.Dependency(-1)]
        ),
        ["Find"] = (
            async (r, ct) => await r.Db<ShopDb>().Customers.FindAsync([-1], ct),
            db => [db.Customers.Dependency(-2)],
            db => [db.Orders.Dependency(-1)]
        ),
        ["tracking query"] = (
            (r, ct) => r.Db<ShopDb>().Orders.AsTracking().ToListAsync(ct),
            db => [db.Orders.Dependency(-1)],
            db => [db.Customers.Dependency(-1)]
        ),
    };

    // Strict-read violations a compute catches, each with the message its caller must still get.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, SluiceReads, CancellationToken, Task> Violate, string Message)
    > _caughtViolations = new()
    {
        ["query on another context"] = ((db, _, ct) => db.Orders.ToListAsync(ct), ForeignContext),
        ["untrackable query"] = (
            (_, r, ct) => r.Db<ShopDb>().CustomerTotals.ToListAsync(ct),
            "*keyless*"
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
                        return await db.Customers.ToListAsync(fetchCt);
                    }
                ),
            Messages.InTransaction
        ),
        ["write in a fused fetch"] = (
            (db, r, _) =>
                r.From(
                    _totals.All,
                    fetchCt =>
                    {
                        db.Customers.Add(new Customer { Name = "Cy" });
                        return db.SaveChangesAsync(fetchCt);
                    }
                ),
            Write
        ),
    };

    // Helper queries that read only their own rows, each with what it reads from the rows AddOwnRows adds.
    private static readonly Dictionary<
        string,
        (Func<SluiceReads, OwnRows, CancellationToken, Task<string>> Read, string Expected)
    > _ownRows = new()
    {
        ["an entity with its owned values and JSON column"] = (
            async (r, rows, ct) =>
            {
                var parcel = await r.Entity(r.Db<ShopDb>().Parcels, rows.Parcel).SingleAsync(ct);
                return $"{parcel.Address.Street}/{parcel.Address.Contact.Phone}/{parcel.Stops.Count}/{parcel.Label.Text}";
            },
            "1 Loop Rd/555/2/Fragile"
        ),
        ["owned values filtered, ordered and projected"] = (
            (r, rows, ct) =>
                r.Children(r.Db<ShopDb>().Parcels, p => p.CustomerId, rows.Ada)
                    .Where(p => p.Address.Street != "")
                    .OrderBy(p => p.Id)
                    .Select(p => $"{p.Address.Contact.Phone}/{p.Stops.Count}/{p.Label.Text}")
                    .SingleAsync(ct),
            "555/2/Fragile"
        ),
        ["an owned collection under identity resolution"] = (
            (r, rows, ct) =>
                r.Db<ShopDb>()
                    .Parcels.AsNoTrackingWithIdentityResolution()
                    .Where(p => p.Id == rows.Parcel)
                    .Select(p => p.Stops.OrderBy(s => s.Place).First().Place)
                    .SingleAsync(ct),
            "Depot"
        ),
        ["AsTracking(NoTracking), paged"] = (
            async (r, rows, ct) =>
                $"{await r.Entity(r.Db<ShopDb>().Parcels, rows.Parcel)
                    .AsTracking(QueryTrackingBehavior.NoTracking)
                    .Skip(0)
                    .Take(1)
                    .CountAsync(ct)}",
            "1"
        ),
        ["an owned collection in a split query"] = (
            async (r, rows, ct) =>
                $"{(await r.Entity(r.Db<ShopDb>().Parcels, rows.Parcel).AsSplitQuery().SingleAsync(ct)).Stops.Count}",
            "2"
        ),
        ["a projection of an entity that loads lazily"] = (
            async (r, rows, ct) =>
                $"{(await r.Entity(r.Db<ShopDb>().Reviews, rows.Review).Select(x => new { x.CustomerId }).SingleAsync(ct)).CustomerId == rows.Ada}",
            "True"
        ),
        ["collections of plain values and a built-in function"] = (
            (r, rows, ct) =>
            {
                int[] ids = [rows.Ada];
                return r.Db<ShopDb>()
                    .Customers.Where(c =>
                        ids.Contains(c.Id) && new[] { rows.Ada, -1 }.Contains(c.Id)
                    )
                    .Select(c => ShopDb.Upper(c.Name))
                    .SingleAsync(ct);
            },
            "ADA"
        ),
    };

    public static TheoryData<string> Untrackable => new(_untrackable.Keys);

    public static TheoryData<string> Reaching => new(_reaching.Keys);

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
    [MemberData(nameof(Untrackable))]
    public async Task A_command_Sluice_cannot_track_throws_inside_a_compute_and_runs_outside_one(
        string kind
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (command, message) = _untrackable[kind];

        var inside = () => CachedRead.Of(node.Cache, (read, ct) => command(read.Db<ShopDb>(), ct));

        await inside.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);

        // Some fail in the database or in EF (the test schema has no such function), but not in Sluice.
        var outside = await Record.ExceptionAsync(() => command(db, CancellationToken.None));
        outside?.Message.Should().NotContain("Sluice");
    }

    [Theory]
    [MemberData(nameof(Reaching))]
    public async Task A_query_is_evicted_by_a_write_to_any_type_it_reads_and_not_by_others(
        string shape
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (read, reached, unread) = _reaching[shape];

        (await CachedRead.IsEvictedBy(read, reached(db))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(read, unread(db))).Should().BeFalse();
    }

    [Fact]
    public async Task A_plain_query_is_evicted_by_any_write_to_its_type_and_by_AllDependencies_but_not_by_another_type()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();

        static Task ReadAll(SluiceReads read, CancellationToken ct) =>
            read.Db<ShopDb>().Customers.ToListAsync(ct);

        (await CachedRead.IsEvictedBy(ReadAll, db.Customers.Dependency(ada))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadAll, db.Customers.Dependency(-1))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadAll, db.Customers.AllDependencies())).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadAll, db.Orders.AllDependencies())).Should().BeFalse();
    }

    [Fact]
    public async Task A_query_that_reads_whole_types_logs_them_once_and_one_that_reads_only_through_helpers_does_not()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bea) = await db.AddCustomers();

        foreach (var id in new[] { ada, bea })
        {
            await node.Cache.GetOrSetAsync(
                _wholeTypes,
                id,
                async Task<int> (read, ct) =>
                    (
                        await read.Children(read.Db<ShopDb>().Orders, o => o.CustomerId, id)
                            .Select(o => o.Customer!.Name)
                            .ToListAsync(ct)
                    ).Count
            );
            await node.Cache.GetOrSetAsync(
                _helperOnly,
                id,
                async Task<int> (read, ct) =>
                    await read.Children(read.Db<ShopDb>().Orders, o => o.CustomerId, id)
                        .CountAsync(ct)
            );
        }

        var logged = node
            .Logs.GetSnapshot()
            .Where(record => record.Message.Contains("reads every row of"))
            .ToList();
        logged.Should().ContainSingle();
        logged[0].Level.Should().Be(LogLevel.Information);
        logged[0]
            .Message.Should()
            .Contain("ef-tracked-whole-types reads every row of Customer, so")
            .And.Contain("read.Entity or read.Children");
    }

    [Fact]
    public async Task A_query_compiled_outside_a_compute_records_its_reads_inside_one()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        // EF caches the compiled query, so the compute runs the one compiled here.
        await db.Orders.Where(o => o.Customer!.Name == "Compiled outside").ToListAsync();

        (
            await CachedRead.IsEvictedBy(
                (read, ct) =>
                    read.Db<ShopDb>()
                        .Orders.Where(o => o.Customer!.Name == "Compiled outside")
                        .ToListAsync(ct),
                db.Customers.Dependency(-1)
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task Explicit_loading_on_the_compute_s_context_records_the_type_it_loads()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        var order = new Order { CustomerId = ada, Item = "Tea" };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        string? name = null;

        async Task Load(SluiceReads read, CancellationToken ct)
        {
            var compute = read.Db<ShopDb>();
            var loaded = await compute.Orders.AsTracking().SingleAsync(o => o.Id == order.Id, ct);
            await compute.Entry(loaded).Reference(o => o.Customer).LoadAsync(ct);
            name = loaded.Customer!.Name;
        }

        (await CachedRead.IsEvictedBy(Load, db.Customers.Dependency(-1))).Should().BeTrue();
        name.Should().Be("Ada");
    }

    [Fact]
    public async Task A_query_on_a_context_the_compute_did_not_get_from_read_Db_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var plain = () => CachedRead.Of(node.Cache, (_, ct) => db.Orders.ToListAsync(ct));
        var helper = () =>
            CachedRead.Of(node.Cache, (read, ct) => read.Entity(db.Customers, 1).ToListAsync(ct));
        var lazy = () =>
            CachedRead.Of(
                node.Cache,
                async (_, ct) => await db.Reviews.Select(r => r.Id).ToListAsync(ct)
            );

        await plain.Should().ThrowAsync<InvalidOperationException>().WithMessage(ForeignContext);
        await helper.Should().ThrowAsync<InvalidOperationException>().WithMessage(ForeignContext);
        await lazy.Should().ThrowAsync<InvalidOperationException>().WithMessage(ForeignContext);
    }

    [Theory]
    [MemberData(nameof(CaughtViolations))]
    public async Task A_compute_that_catches_a_violation_caches_nothing_and_its_caller_still_gets_it(
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
            await read.Db<ShopDb>().Customers.ToListAsync(ct);
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
    [MemberData(nameof(ShapesOfOwnRows))]
    public async Task A_read_helper_query_may_read_owned_values_and_shape_its_own_rows(string shape)
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var rows = await AddOwnRows(db);
        var (read, expected) = _ownRows[shape];
        var value = "";

        await CachedRead.Of(node.Cache, async (r, ct) => value = await read(r, rows, ct));

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
                    await r.Children(r.Db<ShopDb>().Tickets, t => t.CustomerId, ada)
                        .IgnoreQueryFilters(["customer"])
                        .CountAsync(ct),
                    await r.Children(r.Db<ShopDb>().Tickets, t => t.CustomerId, ada)
                        .IgnoreQueryFilters()
                        .CountAsync(ct),
                    await r.Db<ShopDb>()
                        .Memos.IgnoreQueryFilters()
                        .Where(m => m.CustomerId == ada)
                        .CountAsync(ct),
                    await r.Entity(r.Db<ShopDb>().Accounts, account.Id)
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

        async Task ReadRex(SluiceReads r, CancellationToken ct) =>
            read = await r.Entity(r.Db<ShopDb>().Dogs, rex.Id).SingleAsync(ct);

        await CachedRead.Of(node.Cache, ReadRex);

        read!.Owner.Should().BeNull("the helper reads only the dog's own row");
        (await CachedRead.IsEvictedBy(ReadRex, db.Customers.Dependency(-1)))
            .Should()
            .BeFalse("it doesn't read customers");
        (await db.Dogs.AsNoTracking().SingleAsync(d => d.Id == rex.Id))
            .Owner.Should()
            .NotBeNull("a plain query does auto-include it");
    }

    [Fact]
    public async Task A_query_that_opens_a_transaction_on_the_compute_s_context_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();

        var ef = () =>
            CachedRead.Of(
                node.Cache,
                async (r, ct) =>
                {
                    var compute = r.Db<ShopDb>();
                    await using var transaction = await compute.Database.BeginTransactionAsync(ct);
                    await r.Entity(compute.Customers, ada).SingleAsync(ct);
                }
            );
        var scope = () =>
            CachedRead.Of(
                node.Cache,
                async (r, ct) =>
                {
                    using var transaction = new TransactionScope(
                        TransactionScopeAsyncFlowOption.Enabled
                    );
                    await r.Entity(r.Db<ShopDb>().Customers, ada).SingleAsync(ct);
                }
            );

        await ef.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(Messages.InTransaction);
        await scope
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(Messages.InTransaction);
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
    public async Task A_query_after_a_read_From_that_only_records_is_still_checked()
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
            .WithMessage(
                $"An EF command (SELECT *{ForeignContext}",
                "the message names the command"
            );
    }

    [Fact]
    public async Task While_fused_fetches_run_side_by_side_each_runs_queries_on_any_context_and_the_compute_s_own_flow_does_not()
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

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(ForeignContext);
        fetched.Should().HaveCount(2, "each fetch ran its own query");
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
            .WithMessage(ForeignContext);
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

    private sealed record OwnRows(int Ada, int Parcel, int Review);
}
