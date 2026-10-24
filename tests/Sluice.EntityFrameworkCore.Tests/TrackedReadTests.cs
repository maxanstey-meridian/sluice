using System.Linq.Expressions;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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
    private const string NotLinq =
        "*isn't a LINQ query*Run it in the fetch of read.From(dependency, fetch)*";
    private const string Write =
        "*Writes can't run inside a compute*run them before or after GetOrSetAsync*";
    private const string WithNoContext =
        "An EF command with no context ran inside a compute (select 1)*can't tell what it read, or which "
        + "transaction it ran in*add or attach entities before or after GetOrSetAsync.";

    // The remedies the analysis's reasons end with.
    private const string Declare =
        "*Run it in the fetch of read.From(dependency, fetch)*invalidate that source when its data changes*";
    private const string Configure =
        "*That state is whatever the context was made with*Set it from the cache key when the compute first asks for "
        + "the context, with read.Db<ShopDb>(db => …), or with read.Db<ShopDb>((db, services) => …) to set a "
        + "scoped service the state is read from. Or filter by hand: *";
    private const string SelectValues = "*Select the values you need instead*";
    private const string ForeignContext = "*didn't get from read.Db<TContext>()*";
    private const string Rename = """update "Customers" set "Name" = 'Cy' where "Id" = -1""";

    private static readonly Query<string, int> _caught = new("ef-tracked-caught");
    private static readonly Query<string, int> _foreign = new("ef-tracked-foreign");
    private static readonly Query<string, object> _wrapped = new("ef-tracked-wrapped");
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
        ["scalar with no context"] = (
            (db, ct) => SelectOne(db).ExecuteScalarAsync(NoContext(db), ct),
            WithNoContext
        ),
        ["non-query"] = ((db, _) => Task.FromResult(db.Database.ExecuteSqlRaw(Rename)), Write),
        ["non-query async"] = ((db, ct) => db.Database.ExecuteSqlRawAsync(Rename, ct), Write),
        ["FromSql"] = (
            (db, ct) => db.Orders.FromSqlRaw("""select * from "Orders" """).ToListAsync(ct),
            "*Order through FromSql" + Declare
        ),
        ["SqlQuery"] = (
            (db, ct) => db.Database.SqlQueryRaw<int>("""select 1 as "Value" """).ToListAsync(ct),
            "*SqlQuery<Int32>" + Declare
        ),
        ["FromSql in a subquery"] = (
            (db, ct) =>
                db
                    .Orders.Where(o =>
                        db.Orders.FromSqlRaw("""select * from "Orders" """).Any(x => x.Id == o.Id)
                    )
                    .ToListAsync(ct),
            "*Order through FromSql" + Declare
        ),
        ["SqlQuery in a subquery"] = (
            (db, ct) =>
                db
                    .Orders.Where(o =>
                        db.Database.SqlQueryRaw<int>("""select 1 as "Value" """)
                            .Contains(o.CustomerId)
                    )
                    .ToListAsync(ct),
            "*SqlQuery<Int32>" + Declare
        ),
        ["database function of the app's"] = (
            (db, ct) => db.Orders.Select(o => ShopDb.CustomerName(o.CustomerId)).ToListAsync(ct),
            "*database function customer_name" + Declare
        ),
        ["keyless view"] = ((db, ct) => db.CustomerTotals.ToListAsync(ct), "*keyless" + Declare),
        ["table read through a view"] = (
            (db, ct) => db.Suppliers.ToListAsync(ct),
            "*view 'supplier_names'" + Declare
        ),
        ["type mapped to a SQL query"] = (
            (db, ct) => db.TopCustomers.ToListAsync(ct),
            "*SQL query" + Declare
        ),
        ["type mapped to a function"] = (
            (db, ct) => db.RecentOrders.ToListAsync(ct),
            "*function 'recent_orders'" + Declare
        ),
        ["root whose derived type is a view"] = (
            (db, ct) => db.Promotions.ToListAsync(ct),
            "*LivePromotion, which is mapped to the view 'live_promotions'*"
        ),
        ["derived type mapped to a view"] = (
            (db, ct) => db.Set<LivePromotion>().ToListAsync(ct),
            "*LivePromotion, which is mapped to the view 'live_promotions'*"
        ),
        ["query filter reading the context"] = (
            (db, ct) => db.Accounts.ToListAsync(ct),
            "*Account's query filter reads the context's state (ShopDb.TenantId)*" + Configure
        ),
        ["lazily loading entities"] = (
            (db, ct) => db.Reviews.ToListAsync(ct),
            "*returns Review entities*Review entities load their navigations lazily" + SelectValues
        ),
        ["entities with a lazy loader property"] = (
            (db, ct) => db.Receipts.ToListAsync(ct),
            "*returns Receipt entities" + SelectValues
        ),
        ["lazily loading entities in an anonymous type"] = (
            (db, ct) => db.Reviews.Select(x => new { x.Id, Review = x }).ToListAsync(ct),
            "*returns Review entities" + SelectValues
        ),
        ["lazily loading entities in a nested collection"] = (
            (db, ct) =>
                db
                    .Customers.Select(c => db.Reviews.Where(r => r.CustomerId == c.Id).ToList())
                    .ToListAsync(ct),
            "*returns Review entities" + SelectValues
        ),
        ["entities including entities that load lazily"] = (
            (db, ct) =>
                db
                    .Awards.IgnoreAutoIncludes()
                    .Include(a => a.Review)
                    .OrderBy(a => a.Id)
                    .ToListAsync(ct),
            "*returns Award entities*Review entities, which load their navigations lazily"
                + SelectValues
        ),
        ["entities auto-including entities that load lazily"] = (
            (db, ct) => db.Awards.ToListAsync(ct),
            "*returns Award entities*Review entities, which load their navigations lazily"
                + SelectValues
        ),
        ["entities with a lazy loader delegate"] = (
            (db, ct) => db.Ratings.ToListAsync(ct),
            "*returns Rating entities*Rating entities load their navigations lazily" + SelectValues
        ),
        ["owners of a lazily loading owned value"] = (
            (db, ct) => db.Deliveries.ToListAsync(ct),
            "*returns Delivery entities*Signature entities load their navigations lazily"
                + SelectValues
        ),
        ["a lazily loading owned value"] = (
            (db, ct) => db.Deliveries.Select(d => d.Signature).ToListAsync(ct),
            "*returns Signature entities*Signature entities load their navigations lazily"
                + SelectValues
        ),
        ["entities holding their context"] = (
            (db, ct) => db.Baskets.ToListAsync(ct),
            "*returns Basket entities*Basket entities hold the context that loaded them"
                + SelectValues
        ),
        ["entities filtered by entities that load lazily"] = (
            (db, ct) =>
                db.Customers.Where(c => db.Reviews.Any(r => r.CustomerId == c.Id)).ToListAsync(ct),
            "*returns Customer entities*Review entities, which load their navigations lazily*"
                + "Sluice doesn't follow which of the entities it reads the result holds"
                + SelectValues
        ),
        ["query filter reading a static property"] = (
            (db, ct) => LedgersFilteredBy(db, "accessor").ToListAsync(ct),
            """*Ledger's query filter "accessor" reads shared state (TenantAccessor.Current)"""
                + IgnoreJust("accessor")
        ),
        ["query filter calling a static method"] = (
            (db, ct) => LedgersFilteredBy(db, "method").ToListAsync(ct),
            """*Ledger's query filter "method" reads shared state (TenantAccessor.Read())"""
                + IgnoreJust("method")
        ),
        ["query filter reading the clock"] = (
            (db, ct) => LedgersFilteredBy(db, "clock").ToListAsync(ct),
            """*Ledger's query filter "clock" reads shared state (DateTime.UtcNow)"""
                + IgnoreJust("clock")
        ),
        ["query filter reading an object it captured"] = (
            (db, ct) => LedgersFilteredBy(db, "captured").ToListAsync(ct),
            """*Ledger's query filter "captured" reads shared state (the captured tenants.Current)"""
                + IgnoreJust("captured")
        ),
        ["query filter reading a static field that isn't readonly"] = (
            (db, ct) => LedgersFilteredBy(db, "static field").ToListAsync(ct),
            """*Ledger's query filter "static field" reads shared state (TenantAccessor.Fallback)"""
                + IgnoreJust("static field")
        ),
        ["query filter reading a readonly static AsyncLocal"] = (
            (db, ct) => LedgersFilteredBy(db, "async local").ToListAsync(ct),
            """*Ledger's query filter "async local" reads shared state (TenantAccessor.Flow.Value)"""
                + IgnoreJust("async local")
        ),
        ["query filter reading an object it makes"] = (
            (db, ct) => LedgersFilteredBy(db, "new object").ToListAsync(ct),
            """*Ledger's query filter "new object" reads shared state (TenantSource.Current)"""
                + IgnoreJust("new object")
        ),
        ["query filter reading a captured struct holding an object"] = (
            (db, ct) => LedgersFilteredBy(db, "struct").ToListAsync(ct),
            """*Ledger's query filter "struct" reads shared state (the captured scope.Item1)"""
                + IgnoreJust("struct")
        ),
        ["query filter reading a readonly field of the context"] = (
            (db, ct) => LedgersFilteredBy(db, "readonly field").ToListAsync(ct),
            """*Ledger's query filter "readonly field" reads the context's state through ShopDb._fixedTenant, """
                + "which read.Db's configure can't set*settable field or auto-property*"
                + """Or filter by hand: ignore just that filter with IgnoreQueryFilters(["readonly field"])*"""
        ),
        ["query filter reading an init-only property of the context"] = (
            (db, ct) => LedgersFilteredBy(db, "init property").ToListAsync(ct),
            """*Ledger's query filter "init property" reads the context's state through ShopDb.SeededTenant, """
                + "which read.Db's configure can't set*settable field or auto-property*"
                + """Or filter by hand: ignore just that filter with IgnoreQueryFilters(["init property"])*"""
        ),
        ["query filter reading a get-only property of the context"] = (
            (db, ct) => LedgersFilteredBy(db, "get-only property").ToListAsync(ct),
            """*Ledger's query filter "get-only property" reads the context's state through ShopDb.DefaultTenant, """
                + "which read.Db's configure can't set*"
        ),
        ["query filter reading a struct the context holds"] = (
            (db, ct) => LedgersFilteredBy(db, "context struct").ToListAsync(ct),
            """*Ledger's query filter "context struct" reads the context's state (*""" + Configure
        ),
        ["query filter calling a built-in database function given no column"] = (
            (db, ct) => LedgersFilteredBy(db, "database function").ToListAsync(ct),
            """*Ledger's query filter "database function" reads shared state (the database function current_setting)"""
                + IgnoreJust("database function")
        ),
        ["query filter calling a method of the object configuring it"] = (
            (db, ct) => LedgersFilteredBy(db, "instance method").ToListAsync(ct),
            """*Ledger's query filter "instance method" reads shared state (LedgerFilters.Tenant())"""
                + IgnoreJust("instance method")
        ),
        ["query filter reading a field of the object configuring it"] = (
            (db, ct) => LedgersFilteredBy(db, "field").ToListAsync(ct),
            """*Ledger's query filter "field" reads shared state (LedgerFilters.TenantId)"""
                + IgnoreJust("field")
        ),
        ["two named query filters reading shared state"] = (
            (db, ct) => LedgersFilteredBy(db, "accessor", "clock").ToListAsync(ct),
            "*2 query filters read state that can differ between calls*"
                + """Ledger's query filter "accessor" reads shared state (TenantAccessor.Current); """
                + """Ledger's query filter "clock" reads shared state (DateTime.UtcNow). """
                + """Ignore just those filters with IgnoreQueryFilters(["accessor", "clock"]), so any other """
                + "filters still apply, then filter by what they read yourself, and put it in the cache key*"
        ),
        ["named query filter reading the context"] = (
            (db, ct) => db.Issues.ToListAsync(ct),
            """*Issue's query filter "Tenant" reads the context's state (ShopDb.TenantId)*"""
                + Configure
                + """ignore just that filter with IgnoreQueryFilters(["Tenant"]), so any other filters still apply*"""
        ),
        ["named query filter reading the context on a type reached through a navigation"] = (
            (db, ct) =>
                db.Watches.IgnoreAutoIncludes().Select(w => w.Issue!.IsDeleted).ToListAsync(ct),
            """*Issue's query filter "Tenant" reads the context's state (ShopDb.TenantId)*"""
                + Configure
        ),
        ["named query filter reading the context on a type it auto-includes"] = (
            (db, ct) => db.Watches.ToListAsync(ct),
            """*Issue's query filter "Tenant" reads the context's state (ShopDb.TenantId)*"""
                + Configure
        ),
        ["anonymous query filter reading the context on a type it includes"] = (
            (db, ct) => db.Watches.IgnoreAutoIncludes().Include(w => w.Account).ToListAsync(ct),
            "*Account's query filter reads the context's state (ShopDb.TenantId)*" + Configure
        ),
        ["named and anonymous query filters reading the context"] = (
            (db, ct) => db.Watches.Include(w => w.Account).ToListAsync(ct),
            "*: Account's query filter reads the context's state (ShopDb.TenantId); "
                + """Issue's query filter "Tenant" reads the context's state (ShopDb.TenantId).*"""
                + Configure
                + "Account's query filter has no name, so only IgnoreQueryFilters() ignores it*not just these. "
                + """Name it, as in HasQueryFilter("Tenant", …), and ignore just these with """
                + """IgnoreQueryFilters(["Tenant"]), so any other filters still apply*"""
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
        ["auto-included many-to-many navigation"] = (
            (r, ct) => r.Db<ShopDb>().Playlists.ToListAsync(ct),
            db => [db.Songs.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["auto-included many-to-many join rows"] = (
            (r, ct) => r.Db<ShopDb>().Playlists.ToListAsync(ct),
            db => db.PlaylistSongs.AllDependencies(),
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation an owned value auto-includes"] = (
            (r, ct) => r.Db<ShopDb>().Jukeboxes.ToListAsync(ct),
            db => [db.Songs.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation an auto-included navigation's target auto-includes"] = (
            (r, ct) => r.Db<ShopDb>().Kennels.ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["navigation a derived type's owned value auto-includes"] = (
            (r, ct) => r.Db<ShopDb>().Pets.ToListAsync(ct),
            db => [db.Songs.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["auto-includes and filters despite a query filter that ignores them in its subquery"] = (
            (r, ct) => r.Db<ShopDb>().Vouchers.ToListAsync(ct),
            db => [db.Customers.Dependency(-1)],
            db => [db.Orders.Dependency(-1)]
        ),
        ["query filter reading only what can't change between calls"] = (
            (r, ct) => LedgersFilteredBy(r.Db<ShopDb>()).ToListAsync(ct),
            db => [db.Ledgers.Dependency(-1)],
            db => [db.Coupons.Dependency(-1)]
        ),
        ["entities without the navigation that loads lazily"] = (
            (r, ct) => r.Db<ShopDb>().Awards.IgnoreAutoIncludes().ToListAsync(ct),
            db => [db.Awards.Dependency(-1)],
            db => [db.Reviews.Dependency(-1)]
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
        ["helper query that reads its own type again"] = (
            (r, ct) =>
            {
                var db = r.Db<ShopDb>();
                return r.Children(db.Orders, o => o.CustomerId, -1)
                    .Select(o => db.Orders.Count(other => other.CouponId == o.CouponId))
                    .ToListAsync(ct);
            },
            db => [db.Orders.Dependency(-2)],
            db => [db.Coupons.Dependency(-1)]
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

    // Commands on a context without UseSluice, each with the message it must fail with inside a compute.
    private static readonly Dictionary<
        string,
        (Func<ShopDb, CancellationToken, Task> Run, string Message)
    > _withoutSluice = new()
    {
        ["ToListAsync"] = ((db, ct) => db.Customers.ToListAsync(ct), WithoutSluice),
        ["ToList"] = ((db, _) => Task.FromResult(db.Customers.ToList()), WithoutSluice),
        ["ExecuteSqlRaw"] = ((db, _) => Task.FromResult(db.Database.ExecuteSqlRaw(Rename)), Write),

        // EF's raw command builder given no context, on this context's connection and logger.
        ["a read with no context"] = (
            (db, ct) => SelectOne(db).ExecuteScalarAsync(NoContext(db), ct),
            WithNoContext
        ),
        ["a write with no context"] = (
            (db, ct) =>
                db.GetService<IRawSqlCommandBuilder>()
                    .Build(Rename)
                    .ExecuteNonQueryAsync(NoContext(db, CommandSource.ExecuteSqlRaw), ct),
            WithNoContext.Replace("(select 1)", "(update*)")
        ),
    };

    private const string WithoutSluice =
        "An EF command (SELECT *) ran inside a compute on a ShopDb, which isn't configured with UseSluice, so "
        + "Sluice can't record what it read*Add .UseSluice(services) to ShopDb's options and query it through "
        + "read.Db<ShopDb>(), or run it in the fetch of read.From(dependency, fetch)*";

    public static TheoryData<string> CommandsWithoutSluice => new(_withoutSluice.Keys);

    // Values holding what a cached value mustn't, built in a compute, each with where the message says it is.
    private static readonly Dictionary<
        string,
        (Func<SluiceReads, int, CancellationToken, Task<object>> Compute, string Message)
    > _holding = new()
    {
        ["a constructor-injected loader in a holder"] = (
            async (r, review, ct) =>
                await r.Db<ShopDb>()
                    .Reviews.Where(x => x.Id == review)
                    .Select(x => new ReviewHolder(x))
                    .SingleAsync(ct),
            "Query 'ef-tracked-wrapped' returned a value holding a lazy loader in a Review at "
                + "ReviewHolder.Review._loader, so the cached value would load related rows later, outside the "
                + "compute and with no dependency recorded. Select the values you need into your own type instead "
                + "of the entity."
        ),
        ["a service-property loader in a DTO"] = (
            async (r, _, ct) =>
                await r.Db<ShopDb>().Receipts.Select(x => new ReceiptView(x.Id, x)).FirstAsync(ct),
            "*holding a lazy loader in a Receipt at ReceiptView.Receipt.Loader*"
        ),
        ["the compute's own context, boxed"] = (
            async (r, _, ct) =>
            {
                var db = r.Db<ShopDb>();
                await db.Customers.CountAsync(ct);
                return new List<object> { 1, new Boxed(db) };
            },
            "Query 'ef-tracked-wrapped' returned a value holding a DbContext in a Boxed at "
                + "List<Object>._items[1].Context, so the cached value would keep that context*"
        ),
    };

    public static TheoryData<string> ValuesHolding => new(_holding.Keys);

    // Values holding what a cached value mustn't, where no UseSluice context ever analyses a model with lazy entities.
    private static readonly Dictionary<
        string,
        (Func<SluiceReads, LazyHelpdeskDb, CancellationToken, Task<object>> Compute, string Message)
    > _holdingFromAnyContext = new()
    {
        ["a lazy entity from a context without UseSluice, in a fused fetch"] = (
            (r, lazy, _) =>
                r.From(
                    _totals.All,
                    async fetchCt =>
                        (object)new TenantHolder(await lazy.Tenants.FirstAsync(fetchCt))
                ),
            "Query 'ef-tracked-wrapped' returned a value holding a lazy loader in a LazyHelpTenant at "
                + "TenantHolder.Tenant._loader, so the cached value would load related rows later*"
        ),
        ["the compute's own context, of a model with no lazy entities, boxed"] = (
            async (r, _, ct) =>
            {
                var db = r.Db<PooledHelpdeskDb>();
                await db.Set<HelpTenant>().CountAsync(ct);
                return new Boxed(db);
            },
            "Query 'ef-tracked-wrapped' returned a value holding a DbContext in a Boxed at Boxed.Context, so the "
                + "cached value would keep that context*"
        ),
        ["a context without UseSluice that ran no command, boxed in a fused fetch"] = (
            (r, lazy, _) => r.From(_totals.All, _ => Task.FromResult<object>(new Boxed(lazy))),
            "*returned a value holding a DbContext in a Boxed at Boxed.Context*"
        ),
    };

    public static TheoryData<string> ValuesHoldingFromAnyContext =>
        new(_holdingFromAnyContext.Keys);

    // Values that capture the execution context, whose async state holds the compute and its own context, made in a
    // compute that has its context. None holds the context itself.
    private static readonly Dictionary<string, Func<object>> _capturingTheFlow = new()
    {
        ["a captured execution context"] = () => new Holding(ExecutionContext.Capture()!),
        ["a cancellation token with a registration"] = () =>
        {
            var source = new CancellationTokenSource();
            source.Token.Register(static () => { });
            return new Holding(source.Token);
        },
        ["a pending task with a continuation"] = () =>
        {
            var pending = new TaskCompletionSource();
            _ = pending.Task.ContinueWith(static _ => { }, TaskScheduler.Default);
            return new Holding(pending.Task);
        },
    };

    public static TheoryData<string> ValuesCapturingTheFlow => new(_capturingTheFlow.Keys);

    // Generic code reaches the navigation through the interface its type parameter is constrained to.
    private static IQueryable<string> CustomerNames<T>(IQueryable<T> rows)
        where T : IHasCustomer => rows.Select(row => row.Customer!.Name);

    // Ledgers under the plain query filter and those named: Ledger's other filters each read shared state.
    private static IQueryable<Ledger> LedgersFilteredBy(ShopDb db, params string[] filters) =>
        db.Ledgers.IgnoreQueryFilters([
            .. db
                .Model.FindEntityType(typeof(Ledger))!
                .GetDeclaredQueryFilters()
                .Select(declared => declared.Key!)
                .Where(key => key != "plain" && !filters.Contains(key)),
        ]);

    // The remedy for one named filter the analysis refuses: ignore just that one.
    private static string IgnoreJust(string filter) =>
        $"""*. Ignore just that filter with IgnoreQueryFilters(["{filter}"]), so any other filters still apply, """
        + "then filter by that value yourself, and put it in the cache key*";

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

    // A command's inputs with the context's logger, so EF raises its events, but no context.
    private static RelationalCommandParameterObject NoContext(
        ShopDb db,
        CommandSource source = CommandSource.Unknown
    ) =>
        new(
            db.GetService<IRelationalConnection>(),
            null,
            null,
            null,
            db.GetService<IRelationalCommandDiagnosticsLogger>(),
            source
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
            .Where(record => record.Message.Contains("depends on every row of"))
            .ToList();
        logged.Should().ContainSingle();
        logged[0].Level.Should().Be(LogLevel.Information);
        logged[0]
            .Message.Should()
            .Contain("ef-tracked-whole-types depends on every row of Customer, so")
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

    [Fact]
    public async Task A_read_helper_called_with_another_compute_s_read_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        SluiceReads? other = null;
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A compute that lets its read escape, and is still running when another compute uses it.
        var first = CachedRead.Of(
            node.Cache,
            async (read, ct) =>
            {
                await read.Entity(read.Db<ShopDb>().Customers, -2).ToListAsync(ct);
                other = read;
                captured.SetResult();
                await release.Task;
            }
        );
        await captured.Task;
        var helper = () =>
            CachedRead.Of(
                node.Cache,
                async (read, ct) =>
                {
                    await read.Entity(read.Db<ShopDb>().Customers, -1).ToListAsync(ct);
                    await other!.Entity(read.Db<ShopDb>().Orders, -1).ToListAsync(ct);
                }
            );
        var context = () =>
            CachedRead.Of(
                node.Cache,
                async (read, ct) =>
                {
                    await read.Entity(read.Db<ShopDb>().Customers, -1).ToListAsync(ct);
                    await other!.Db<ShopDb>().Orders.ToListAsync(ct);
                }
            );

        await helper
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Use the read the compute was given*");
        await context
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Use the read the compute was given*");
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task A_query_on_a_table_mapped_type_whose_TPC_root_has_a_view_is_cached_and_evicted_by_a_write_to_it()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var stored = await CachedRead.Of(
            node.Cache,
            (read, ct) => read.Db<ShopDb>().Set<StoredPromotion>().ToListAsync(ct)
        );

        (await stored.WasEvicted()).Should().BeFalse("it was cached");
        db.Add(new StoredPromotion { Code = "TEN" });
        await db.SaveChangesAsync();
        (await stored.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_query_the_analysis_fails_on_is_tagged_as_untrackable_instead_of_throwing()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var customer = Expression.Parameter(typeof(Customer), "c");
        var query = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Where),
            [typeof(Customer)],
            db.Customers.AsQueryable().Expression,
            Expression.Quote(Expression.Lambda(new Unanalysable(), customer))
        );

        // The analysis reads only the event's context, so its logging parts are left out.
        var tagged = (MethodCallExpression)
            TrackedReads.Instance.QueryCompilationStarting(
                query,
                new QueryExpressionEventData(null!, null!, db, query, null!)
            );

        ((MethodCallExpression)tagged.Arguments[0])
            .Arguments[1]
            .Should()
            .BeOfType<ConstantExpression>()
            .Which.Value.Should()
            .Be(
                "Sluice can't track: Sluice couldn't work out what it reads "
                    + "(InvalidOperationException: The analysis failed on this.). Run it in the fetch of "
                    + "read.From(dependency, fetch), with a dependency on a Source you declare, and invalidate that "
                    + "source when its data changes. This is a bug in Sluice's analysis; please report it with the "
                    + "query"
            );
    }

    // EF can't translate a method of the app's given a lambda, so it refuses the query before any command runs; the
    // analysis refuses it too, rather than taking the lambda's own parameter for the rows.
    [Fact]
    public async Task A_query_filter_calling_a_static_method_with_a_lambda_that_does_not_read_the_rows_is_tagged_as_untrackable()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var query = LedgersFilteredBy(db, "lambda").Expression;

        var tagged = TrackedReads.Instance.QueryCompilationStarting(
            query,
            new QueryExpressionEventData(null!, null!, db, query, null!)
        );

        tagged
            .ToString()
            .Should()
            .Match(
                """*Sluice can't track: Ledger's query filter "lambda" reads shared state (TenantAccessor.Map())"""
                    + IgnoreJust("lambda")
            );
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

    // A multi-tenant app with soft delete, as the refusal of its tenant filter says to read it: ignoring just that
    // filter keeps soft delete, which IgnoreQueryFilters() without names would drop.
    [Fact]
    public async Task A_query_that_ignores_just_the_refused_named_filter_keeps_the_others_and_is_evicted_by_a_write()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var tenant = Random.Shared.Next(1_000, int.MaxValue);
        var open = new Issue { TenantId = tenant };
        db.Issues.AddRange(
            open,
            new Issue { TenantId = tenant },
            new Issue { TenantId = tenant, IsDeleted = true },
            new Issue { TenantId = tenant + 1 }
        );
        await db.SaveChangesAsync();
        var count = 0;

        IQueryable<int> IssuesOf(ShopDb context) =>
            context
                .Issues.IgnoreQueryFilters(["Tenant"])
                .Where(issue => issue.TenantId == tenant)
                .Select(issue => issue.Id);

        var cached = await CachedRead.Of(
            node.Cache,
            async (read, ct) => count = await IssuesOf(read.Db<ShopDb>()).CountAsync(ct)
        );

        count.Should().Be(2, "the deleted issue and the other tenant's are left out");
        IssuesOf(db).ToQueryString().Should().Contain("\"IsDeleted\"", "SoftDelete still applies");
        (await db.Issues.IgnoreQueryFilters().CountAsync(issue => issue.TenantId == tenant))
            .Should()
            .Be(3, "IgnoreQueryFilters() without names drops SoftDelete too");

        open.IsDeleted = true;
        await db.SaveChangesAsync();

        (await cached.WasEvicted()).Should().BeTrue();
        count.Should().Be(1);
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
    public async Task A_fused_fetch_declared_with_sets_AllDependencies_is_evicted_by_EF_writes_to_those_types()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();

        // The declared route for a view, as the docs show it: no invalidation by hand.
        var totals = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
            {
                var computeDb = read.Db<ShopDb>();
                return read.From(
                    [
                        .. computeDb.Customers.AllDependencies(),
                        .. computeDb.Orders.AllDependencies(),
                    ],
                    fetchCt => computeDb.CustomerTotals.ToListAsync(fetchCt)
                );
            }
        );

        db.Tags.Add(new Tag { Name = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        (await totals.WasEvicted()).Should().BeFalse("a write to another type doesn't evict it");

        db.Orders.Add(new Order { CustomerId = ada, Item = "Tea" });
        await db.SaveChangesAsync();
        (await totals.WasEvicted()).Should().BeTrue("a save of an order evicts it");

        await db
            .Customers.Where(c => c.Id == ada)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Ada Lovelace"));
        (await totals.WasEvicted()).Should().BeTrue("a bulk update of customers evicts it");
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

    // A context without UseSluice records nothing: inside a compute its commands throw, as an untrackable one does.
    [Theory]
    [MemberData(nameof(CommandsWithoutSluice))]
    public async Task A_command_on_a_context_without_UseSluice_throws_inside_a_compute_and_runs_outside_one(
        string kind
    )
    {
        await using var node = new SluiceNode();
        await using var plain = postgres.CreateDb();
        var (command, message) = _withoutSluice[kind];
        var runs = 0;

        async Task<int> Compute(SluiceReads read, CancellationToken ct)
        {
            runs++;
            await read.Db<ShopDb>().Customers.CountAsync(ct);
            try
            {
                await command(plain, ct);
            }
            catch (InvalidOperationException)
            {
                // A compute that falls back when a read fails.
            }

            return runs;
        }

        var call = () => node.Cache.GetOrSetAsync(_foreign, kind, Compute).AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached");
        await command(plain, CancellationToken.None);
    }

    // EF stops raising a context's command events for a second once it finds nobody listening, so a context that has
    // just run a command outside a compute must still be seen inside one.
    [Fact]
    public async Task A_command_on_a_context_without_UseSluice_throws_inside_a_compute_even_right_after_it_ran_outside_one()
    {
        await using var node = new SluiceNode();
        await using var plain = postgres.CreateDb();

        await plain.Customers.CountAsync();
        var call = () => CachedRead.Of(node.Cache, (_, ct) => plain.Customers.ToListAsync(ct));

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(WithoutSluice);
    }

    // A fetch is a declared read, but a write there still publishes mid-compute, and a read inside a transaction still
    // caches rows other callers can't see. A command with no context says neither which context nor which transaction
    // it ran in, so it throws there too.
    [Fact]
    public async Task A_query_on_a_context_without_UseSluice_runs_in_a_fused_fetch_and_a_save_a_read_in_a_transaction_or_a_command_with_no_context_there_throws()
    {
        await using var node = new SluiceNode();
        await using var plain = postgres.CreateDb();

        var fetched = await CachedRead.Of(
            node.Cache,
            (read, _) => read.From(_totals.All, fetchCt => plain.Customers.ToListAsync(fetchCt))
        );
        var noContext = () =>
            CachedRead.Of(
                node.Cache,
                (read, _) =>
                    read.From(
                        _totals.All,
                        fetchCt => SelectOne(plain).ExecuteScalarAsync(NoContext(plain), fetchCt)
                    )
            );
        var save = () =>
            CachedRead.Of(
                node.Cache,
                (read, _) =>
                    read.From(
                        _totals.All,
                        fetchCt =>
                        {
                            plain.Customers.Add(new Customer { Name = "Cy" });
                            return plain.SaveChangesAsync(fetchCt);
                        }
                    )
            );
        var inTransaction = () =>
            CachedRead.Of(
                node.Cache,
                (read, _) => read.From(_totals.All, fetchCt => plain.Customers.ToListAsync(fetchCt))
            );

        (await fetched.WasEvicted()).Should().BeFalse("the fetch's value was cached");
        await noContext.Should().ThrowAsync<InvalidOperationException>().WithMessage(WithNoContext);
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage(Write);
        await using (await plain.Database.BeginTransactionAsync())
        {
            await inTransaction
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(Messages.InTransaction);
        }
    }

    // The walk runs whatever model the value comes from, so it finds a lazy entity no analysis saw, and a context the
    // app put in the value itself, from a model with no lazy entities, or that ran no command at all.
    [Theory]
    [MemberData(nameof(ValuesHoldingFromAnyContext))]
    public async Task A_value_holding_a_lazy_loader_or_a_context_is_refused_whatever_context_it_comes_from(
        string kind
    )
    {
        await HelpdeskModel.EnsureCreated();
        await using var node = new SluiceNode(services: services =>
            services.AddDbContext<PooledHelpdeskDb>(
                (provider, options) =>
                    options
                        .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                        .UseSluice(provider)
            )
        );

        await using var lazy = LazyHelpdeskDb.Create();
        var (compute, message) = _holdingFromAnyContext[kind];
        var runs = 0;

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _wrapped,
                    kind,
                    (r, ct) =>
                    {
                        runs++;
                        return compute(r, lazy, ct);
                    }
                )
                .AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached");
    }

    // A Task, a CancellationToken's registrations and a delegate's state capture the execution context, which reaches
    // the compute's own context through its async state; the value doesn't hold that context, so it's stored.
    [Theory]
    [MemberData(nameof(ValuesCapturingTheFlow))]
    public async Task A_value_holding_a_captured_execution_context_is_stored(string kind)
    {
        await using var node = new SluiceNode();
        var value = _capturingTheFlow[kind];
        var runs = 0;

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _wrapped,
                    kind,
                    async (r, ct) =>
                    {
                        runs++;
                        await r.Db<ShopDb>().Customers.CountAsync(ct);
                        return value();
                    }
                )
                .AsTask();

        await call();
        await call();
        runs.Should().Be(1, "the value was stored");
    }

    // The analysis refuses entities by the query's result type; a lazy entity inside the app's own type, or the
    // context itself, is found in the value before it's stored.
    [Theory]
    [MemberData(nameof(ValuesHolding))]
    public async Task A_lazily_loading_entity_wrapped_in_an_app_type_is_refused_before_it_is_stored(
        string kind
    )
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var rows = await AddOwnRows(db);
        db.Receipts.Add(new Receipt());
        await db.SaveChangesAsync();
        var (compute, message) = _holding[kind];
        var runs = 0;

        var call = () =>
            node
                .Cache.GetOrSetAsync(
                    _wrapped,
                    kind,
                    (r, ct) =>
                    {
                        runs++;
                        return compute(r, rows.Review, ct);
                    }
                )
                .AsTask();

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await call.Should().ThrowAsync<InvalidOperationException>();
        runs.Should().Be(2, "nothing was cached");
    }

    private sealed record ReviewHolder(Review Review);

    private sealed record ReceiptView(int Id, Receipt Receipt);

    private sealed record Boxed(object Context);

    private sealed record Holding(object Value);

    private sealed record TenantHolder(LazyHelpTenant Tenant);

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

    // An expression the analysis fails on, standing for a bug in it.
    private sealed class Unanalysable : Expression
    {
        public override ExpressionType NodeType => ExpressionType.Extension;

        public override Type Type => typeof(bool);

        protected override Expression VisitChildren(ExpressionVisitor visitor) =>
            throw new InvalidOperationException(
                $"The analysis failed{Environment.NewLine}on this."
            );
    }
}
