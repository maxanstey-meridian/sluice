# EF Core

With `Sluice.EntityFrameworkCore`, you don't declare sources for your entities: Sluice works
them out from the EF model. Inside a compute you query a context of the compute's own with
ordinary LINQ, and Sluice records every entity type each query reads. Saves and bulk updates
invalidate what they changed by themselves once they commit. Underneath it's the same API as
everywhere else: queries record ordinary dependencies, and writes call
`SluiceCache.InvalidateAsync`.

## Setup

```csharp
builder.Services.AddSluice();
builder.Services.AddSluiceEntityFrameworkCore();
builder.Services.AddDbContextFactory<ShopDb>(
    (services, options) => options.UseNpgsql(connectionString).UseSluice(services)
);
```

- Register the context however you like: `AddDbContext`, `AddDbContextFactory`,
  `AddDbContextPool` or `AddPooledDbContextFactory`. Each compute opens a DI scope of its own
  and takes `ShopDb` from it, as a request does (see [Reading](#reading)); each of those
  registers `ShopDb` as a scoped service. A pooled registration works too, but a compute's
  context never comes from the pool (see [Pooling and factories](#pooling-and-factories)). If
  you register it more than one way, `UseSluice` runs once per context either way. Registered
  only through a factory class of your own over a context that isn't pooled? Register the
  context too, so the scope can make it:
  `services.AddScoped(sp => sp.GetRequiredService<TenantDbFactory>().CreateDbContext())`. A
  pooled registration needs nothing more: Sluice builds the compute's context from the pool's
  options.
- Register the context scoped (every EF registration's default) or transient, never as a
  singleton. `read.Db` checks, once per context type, that two DI scopes get different
  instances, and throws `InvalidOperationException` before the compute's first query if they
  get the same one: a singleton registration, or a factory that hands out one instance, would
  share one context between every compute and request, and a compute's configure would change
  it under them.
- A context whose constructor takes scoped services (a
  [tenant provider](/guides/strict-reads#tenants-and-other-context-state)) needs a registration
  that makes it in the scope: `AddDbContext`, or `AddDbContextFactory(..., ServiceLifetime.Scoped)`.
  The default singleton factory makes it from the root provider, which refuses scoped services
  in Development and, elsewhere, hands every context one shared instance.
- `UseSluice` needs `SluiceCache` (`AddSluice`), the EF package (`AddSluiceEntityFrameworkCore`,
  called where you call `AddSluice`) and logging registered. ASP.NET Core and the Generic Host register
  logging; a bare `ServiceCollection` needs `AddLogging()`. Without any of them, `UseSluice`
  throws `InvalidOperationException`. `AddSluiceEntityFrameworkCore` also starts Sluice's watch
  for [contexts without `UseSluice`](/guides/strict-reads#a-query-on-another-context) inside a
  compute, so it covers the very first compute.
- Relational providers only: Sluice reads each save's transaction from the relational
  connection. A context on any other provider throws `NotSupportedException` when it saves,
  before it writes anything, and `read.Db` and the read helpers refuse it the same way. The
  test suite runs on PostgreSQL; see [Key folding](#key-folding) for what that means for other
  providers.
- Building options by hand works too: `UseSluice` has a generic overload, so
  `new DbContextOptionsBuilder<ShopDb>().UseNpgsql(connectionString).UseSluice(services).Options`
  is a `DbContextOptions<ShopDb>`.
- A context that calls `UseInternalServiceProvider` can't use Sluice. Recording reads needs an
  interceptor shared by every context, and EF refuses one on such a context.

### Interceptor order

The order of save interceptors doesn't matter. Sluice records what a save writes when EF
initialises the save's first command: after every `SavingChanges` interceptor (soft delete,
auditing, an outbox), wherever it was added, after EF's own change detection, and after the
cascades and orphan deletions EF applies during the save. So whatever those change is
invalidated too.

A query expression interceptor of your own (`IQueryExpressionInterceptor`) must come before
`UseSluice`, in the same options:

```csharp
builder.Services.AddDbContextFactory<ShopDb>(
    (services, options) =>
        options
            .UseNpgsql(connectionString)
            .AddInterceptors(new ShardingQueryRewriter())
            .UseSluice(services)
);
```

EF runs query expression interceptors in the order they were added, those in the context's
`OnConfiguring` last, so one added after `UseSluice`, or in `OnConfiguring`, would rewrite a
query after Sluice had [worked out what it reads](#what-a-query-records), and a table it adds
wouldn't be recorded. `read.Db` and the read helpers throw `InvalidOperationException` for such
a context, naming the interceptor and saying to add it before `UseSluice`.

### Pooling and factories

`AddDbContextPool((services, options) => ...)`, `AddPooledDbContextFactory` and
`AddDbContextFactory` all work, for reads and writes. Each use of a pooled context invalidates
what it wrote, and nothing an earlier use wrote. Contexts sharing a transaction
need nothing more, as long as each uses `UseSluice` with the same `SluiceCache`. See
[Contexts sharing a transaction](/guides/transactions#contexts-sharing-a-transaction).

A compute's context never comes from the pool. EF resets a pooled context's own state between
uses, but not your fields, and not a connection string you set, so what a compute's
[configure](/guides/strict-reads#tenants-and-other-context-state) set (a tenant, a tenant's
database) would still be there for the next use, a request's. Instead, Sluice builds the
compute's context from the pool's options, with its constructor's other services from the
compute's own DI scope, and disposes it when the compute ends. For a pool registered through
EF (`AddDbContextPool`, `AddPooledDbContextFactory`), nothing is leased and no factory of yours
is called. A pool you built by hand (`new PooledDbContextFactory<T>(options)`) is leased once, to
see that it's a pool, and the lease goes back untouched. EF's model and internal services are
shared, so this costs one context
construction per compute that uses one. Requests keep using the pool as before, and you still
set a request's tenant on every lease, as
[EF's docs do for tenants](https://learn.microsoft.com/ef/core/performance/advanced-performance-topics#managing-state-in-pooled-contexts).

## Reading

Inside a compute, `read.Db<ShopDb>()` gives you the compute's own context. Query it with
ordinary LINQ:

```csharp
var computeDb = read.Db<ShopDb>();

var customer = await computeDb.Customers
    .Include(customer => customer.Address)
    .SingleOrDefaultAsync(customer => customer.Id == customerId, ct);

var orders = await computeDb.Orders
    .Where(order => order.CustomerId == customerId)
    .OrderByDescending(order => order.PlacedAt)
    .Select(order => new OrderSummary(order.Id, order.Total, order.Coupon!.Code))
    .ToListAsync(ct);
```

The context comes from a DI scope the compute opens the first time it asks for one, and the
same one comes back for every call in that compute. The scope, and the context with it, is
disposed when the compute ends. The context holds nothing from outside the compute, so it can't
hand back your request's tracked entities or their unsaved edits. Its queries don't track unless
you call `AsTracking()`, and it isn't in your request's transaction, so it reads what's
committed. To set it up before its first query (the tenant its query filters read, or the
tenant's database), pass a configure: `read.Db<ShopDb>(db => db.TenantId = tenantId)`. See
[Tenants](/guides/strict-reads#tenants-and-other-context-state).

### What a query records

Each query records every entity type it reads, and a write to any of them evicts the value.
Sluice works that out from the query when EF compiles it, so everything EF can express counts:

- the sets it starts from, and those it joins, unions or reads in a subquery;
- every navigation it follows, in a filter, a projection or an `Include`, by property, through
  `EF.Property`, through a cast or an interface, or by name;
- many-to-many navigations, which read the join entity too;
- what EF adds by itself: [auto-included](https://learn.microsoft.com/ef/core/querying/related-data/eager#model-configuration-for-auto-including-navigations)
  navigations, unless you call `IgnoreAutoIncludes()`, and each type's query filters, unless you
  ignore them: `IgnoreQueryFilters()` drops all of them, `IgnoreQueryFilters(["Tenant"])` just
  the filters you name;
- `Find`, explicit loading (`Entry(order).Reference(o => o.Customer).LoadAsync()`) and compiled
  queries.

Owned types and JSON columns are stored in their owner's row, so they count as the owner.

A query records the whole of each type, so in the example above, a change to *any* customer,
address, order or coupon evicts the value (if `Address` were an owned type, it would count as
the customer). That's always correct, just broad. When it's too broad, narrow it. So you can
see which queries do this, Sluice logs each one the first time it runs, at `Information`,
naming the query and the types it reads whole (see [Logging](/guides/configuration#logging)).

Dependencies are named after the tables a type is stored in: `Orders` (or `sales.Orders` when
the model gives a schema), `Orders[CustomerId]` for the orders under one customer, and
`Pages(Slug)` for a read by a unique column. An inheritance hierarchy uses its root's table, and
a TPC hierarchy whose root has no table of its own joins its concrete types' tables
(`Circles+Squares`). You'll see these names in Sluice's error messages. So a second class mapped
to the same table, such as a read model in another context, is linked to the first: a write
through either evicts reads through the other, by key, by foreign key, by unique column and
whole. Moving or renaming a class changes nothing. Two classes link only when they agree on the
key columns and spell the schema the same way (`null` and `"public"` are different names); a
read model over just one table of a TPT or TPC hierarchy isn't linked. Renaming a table or its
schema, which comes with a migration, renames its dependencies. These names are the model's: a
`Source` of your own with one of them throws (see
[Names](/guides/sources-and-queries#names)), so reach EF data through `set.Dependency(key)` and
`set.AllDependencies()`.

### Narrowing to rows

Three helpers on `read` start a query that depends on fewer rows:

```csharp
var customer = await read.Entity(computeDb.Customers, customerId).SingleOrDefaultAsync(ct);

var forms = await read.EntityAsync(computeDb.Forms, form => form.Slug, slug, ct);
var form = await forms.SingleOrDefaultAsync(ct);

var orders = await read.Children(computeDb.Orders, order => order.CustomerId, customerId)
    .OrderByDescending(order => order.PlacedAt)
    .Select(order => new OrderSummary(order.Id, order.Total, order.Coupon!.Code))
    .ToListAsync(ct);
```

| Query | Reads | Evicted by a write to |
|---|---|---|
| `computeDb.Customers.Where(…)` | the rows its `Where` matches | any customer |
| `read.Entity(computeDb.Customers, key)` | the row with that primary key | that row |
| `await read.EntityAsync(computeDb.Forms, f => f.Slug, value, ct)` | the row whose unique column holds `value` | that row, or a row taking or leaving `value` |
| `read.Children(computeDb.Orders, fk, key)` | the rows whose foreign key is `key` | any of those rows, or a row joining or leaving the set |

Each returns an `IQueryable<T>` you can filter, order, page and project, on the context
`read.Db` gave you; `EntityAsync` returns it once it has looked up the rows holding the value
([By a unique column](#by-a-unique-column)). Only the helper's own type is narrowed: the `Coupon` navigation in the second
query still depends on every coupon. A read that finds nothing still depends on what it looked
for, so inserting the row later evicts the cached "not found". The helpers don't track unless
you call `AsTracking()`.

The helpers don't load auto-included navigations, anywhere in the query. A helper's query
calls `IgnoreAutoIncludes()`, and EF applies that to the whole query, not just the helper's set.
So the navigations you `Include` lose theirs too: if `Comment.Author` is auto-included,
`read.Entity(computeDb.Tickets, id).Include(t => t.Comments)` loads each comment with a null
`Author`. A plain query combined with a helper's, in a join, a subquery or a `Concat`, loses its
own auto-includes the same way. `Include` each navigation you need explicitly:

```csharp
var ticket = await read.Entity(computeDb.Tickets, id)
    .Include(t => t.Comments)
    .ThenInclude(c => c.Author)
    .SingleOrDefaultAsync(ct);
```

### By a unique column

`await read.EntityAsync(set, column, value, ct)` reads one row by a column that isn't its key, such as a slug
or an email, without depending on every row of the type. A save of another row doesn't evict it;
a save that gives a row that value, changes the row holding it, or moves the value off it, does.
So a cached "not found" for a slug is evicted once a row gets that slug.

The column must be covered by a single-column unique index or alternate key in the EF model
(`HasIndex(f => f.Slug).IsUnique()`). A filtered unique index counts, so a slug unique only among
the rows that have one is fine. Sluice doesn't need the uniqueness to be right; it uses the
model's unique columns to know which values a save must report. A column without one throws
`ArgumentException`, and one unique only together with others (a composite index) throws
`NotSupportedException`. Naming the primary key reads as `read.Entity(set, key)` does. The
value can't be null, nor one the column's value converter stores as null: no read by value
finds a null, so both throw (`ArgumentNullException`, `ArgumentException`). A column whose
converter stores null as a value (EF's internal `convertsNulls`) throws `NotSupportedException`:
a row whose value is null would hold a value a reader finds, but no save would report it.

Each save of a row reports one more value per unique column of its type. Without a row version
the value the database holds when an update or delete runs may not be the one EF loaded, so,
as with `Children` lists, such a save evicts every reader by that column; see
[Row versions](#row-versions).

Before it returns your query, `EntityAsync` runs one more query on the context: the keys of
the rows holding the value (`SELECT id FROM forms WHERE slug = @value`), ignoring query
filters. It records each row it finds by its key, so a write to that row by key evicts the
reader too: `set.Dependency(key)`, a [bulk write by key](#bulk-writes-by-key) even when it
doesn't touch the column, and a keyed delete, which reports the row's key and not its value.
Query filters are ignored so that a row they hide, such as a soft-deleted one, is recorded as
well, and a write that brings it back evicts the reader. A filtered unique index lets rows
outside its filter share a value: each is recorded, and past 100 rows the reader depends on
every row of the type instead. Naming the primary key runs no lookup, and nor does a type with
a composite primary key, which no write by key reaches. Inside a fused fetch
(`read.From(dependency, fetch)`) it looks up on the fetch's context, like anywhere else.

It looks up first so that whatever row your query then finds was either looked up, or got the
value afterwards, which reports the value. A lookup after the query could miss a row deleted by
key in between, and the cached value would keep a row that's gone. A write that moves the value
after the lookup still evicts the entry, as any write during a compute does.

That's one more round trip per call, on the column's index; a cache hit runs neither query. If
the lookup fails, `EntityAsync` throws, and unless the compute catches it, nothing is cached and
the caller gets the exception.

`set.Dependency(key)` reaches only readers that found that row. A reader whose value no row
held, a cached "not found", depends on the value alone, so after a write Sluice can't see that
may have given a row that value, use `set.AllDependencies()`.

A key that comes from public input, such as a slug from a URL, caches every miss. Leave misses
uncached with [`read.DoNotCache()`](/guides/sources-and-queries#leaving-a-value-uncached).

### Keys

- Pass the key as the exact .NET type the model uses: the primary key's type for `Entity`, the
  foreign key property's for `Children`. A typed id with a value converter is passed as the
  typed id (and its converter marked `[SluiceRoundTrips]`: see [Key folding](#key-folding)). Any other type, such as `1L` for an `int` key, throws
  `ArgumentException` naming the type expected. The compiler can't check these keys: the type
  comes from the EF model.
- `Entity` doesn't support composite primary keys: it throws `NotSupportedException`. Use
  `Children`, or a plain query.
- `Children`'s foreign key property may be nullable, but the key you pass can't be null: rows
  with no parent aren't anyone's children. A null throws `ArgumentNullException`, and a key its
  value converter stores as null throws `ArgumentException`. A foreign key whose converter
  stores null as a value (EF's internal `convertsNulls`) throws `NotSupportedException`: a row
  with no parent would hold a value a reader finds, but no save would report it.
- `Children`'s foreign key can be the property (`o => o.CustomerId`), a shadow property named
  by a constant (`o => EF.Property<CustomerId>(o, "CustomerId")`), or a reference navigation to
  the parent (`o => o.Customer`). All three record the same dependency.

### What's refused

Inside a compute, these throw `InvalidOperationException`. Each would cache a value that no
write evicts, or one built from data other callers shouldn't see.
[What a compute can't read](/guides/strict-reads) says why for each, and how to read them
anyway:

- raw SQL (`FromSql`, `SqlQuery`), table-valued functions and database functions of your own;
- types not mapped to a table: keyless types, and types mapped with `ToView`, `ToFunction` or
  `ToSqlQuery`. No tracked write ever invalidates them. An abstract TPC base type is fine when
  every concrete type under it has a table;
- a query filter that reads the context's state, such as a tenant id, unless the compute set it
  with `read.Db<ShopDb>(configure)`, or other state that can change, such as a static tenant
  accessor, a captured service or `DateTime.Now`;
- entities that load lazily, or hold their context, even wrapped in a type of your own: select
  the values you need instead;
- a query on any context other than `read.Db`'s, a context without `UseSluice` included, and
  reading inside a transaction. See [Transactions](/guides/transactions);
- another compute's `read`, captured and used in this one (for `read.From`, `read.Db` or a
  helper);
- writes.

The helpers also refuse types not mapped to a table, with `NotSupportedException`, and a
context without `UseSluice`, with `InvalidOperationException`.

To read a view or a keyless type, or to run Dapper or raw SQL over your tables, use a fused
fetch declared with the dependencies of the entity types underneath it. Then EF writes to those
types evict it by themselves, with no invalidation of yours:

```csharp
var summaries = await read.From(
    [.. computeDb.Orders.AllDependencies(), .. computeDb.Customers.AllDependencies()],
    ct => computeDb.CustomerTotals.Where(row => row.CustomerId == customerId).ToListAsync(ct)
);
```

That depends on every order and customer, so it's broad. See
[The declared route](/guides/strict-reads#the-declared-route).

### Cache projections or detached entities

Cached entities aren't attached to any context; to edit one, load it again on your own context.
Every caller also gets the same object, so never modify one; see
[Shared instances](/guides/configuration#shared-instances-and-auto-clone).

Sluice checks every value before it's stored. A value that holds a lazy loader or a `DbContext` anywhere in
its fields, such as an entity wrapped in your own record, throws `InvalidOperationException`,
naming the query and the path to it, and nothing is cached: the cached value would load related
rows later, outside the compute, or keep the compute's context alive. Select the values you need
into your own type instead.

## Writing

Saves through EF's change tracker just work. Once a save commits, Sluice evicts the cached
reads of every row it inserted, updated or deleted, and of the lists those rows were in or
moved between, and every plain query of those types. [Bulk updates](#bulk-updates-and-deletes)
work too. Two cases are broader. When a delete makes the database delete rows EF never
loaded, or set their foreign key to null (`ON DELETE CASCADE` or `SET NULL` in your model),
Sluice can't tell which rows those were. It evicts every cached read of those types instead.
And unless a type has a [row version](#row-versions), updating or deleting one of its rows
evicts its `Children` lists under every parent, not just the row's own.

In detail, once a save has committed, Sluice invalidates, for each row it added, changed or
deleted:

- the row itself: `Entity` readers of it, and every plain query of its type;
- the parent it had before the save and the one it has after: `Children` readers of each. A
  null foreign key has no parent, so setting a key to or from null evicts just the one. For an
  updated or deleted row without a [row version](#row-versions), the parent before isn't known,
  so every `Children` reader of that foreign key is evicted;
- for a deleted row, its own children: `Children` readers of every foreign key that points at
  it;
- for a deleted row, whatever the database does to other types: see
  [Database cascades](#database-cascades).

A `Children` list is evicted when *any* row in it changes, not only when rows join or leave.
One dependency covers the whole list; that's coarse on purpose.

Sluice reads what changed from the change tracker when EF initialises the save's first command,
once EF has detected changes and applied its cascades and orphan deletions at the timings you
set, and it detects nothing itself, so it never changes what EF saves: with
`AutoDetectChangesEnabled` off, an edit EF doesn't detect is neither saved nor invalidated. A
save with nothing to write runs no command and invalidates nothing. A save that fails, is cancelled or hits a concurrency conflict still
invalidates what it was saving, because part of it may already have committed.

### Row versions

The parent a row had when EF loaded it isn't always the one it has when the save runs. Say
request A loads order 7 under customer 1. Request B moves the order to customer 2 and saves, and
customer 2's order list is cached again, holding order 7. A then edits the order's note and
saves. EF's `UPDATE` matches the row by its key alone, so it succeeds, but the parent A loaded
is customer 1, not 2. A delete has the same gap.

So for a type without a row version, Sluice doesn't trust the parent EF loaded: updating or
deleting one of its rows evicts every `Children` list of that foreign key, under every parent.
Editing one order evicts every customer's cached order list. The same goes for unique columns:
the slug a row had when it was loaded may not be the one it has, so updating or deleting a row
evicts every `read.EntityAsync` by that column, whatever the value. Inserts stay exact, and Sluice
logs once per type and cache (event 3, at Information) when this happens.

A row version is a concurrency token the database changes on every write. With one, EF fails the
save (`DbUpdateConcurrencyException`) unless the row is unchanged since it was loaded, so the
parent it loaded is the real one, and Sluice evicts exactly the old and new parents' lists (and
the old and new values' readers by unique column). On
PostgreSQL, map the system column `xmin`:

```csharp
public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public uint Version { get; set; }
}

modelBuilder.Entity<Order>().Property(order => order.Version).IsRowVersion();
```

On SQL Server, use a `rowversion` column (`[Timestamp] public byte[] Version { get; set; }`).
Saves of a row that changed since it was loaded now fail, so handle
`DbUpdateConcurrencyException` where you save.

What doesn't count:

- **A token only your app changes** (`IsConcurrencyToken()` on a column you bump yourself). A
  bulk update, raw SQL or another service can re-parent the row without bumping it. A foreign
  key that is itself a concurrency token does stay exact, since EF's `WHERE` checks it.
- **A row split across tables** (table-per-type, entity splitting). The token is in one table,
  and the foreign key may be in another. A foreign key that is a concurrency token doesn't help
  either, and Sluice logs event 4 instead of 3.
- **A change only to owned rows** (`OwnsOne` or `OwnsMany`, wherever they're stored). The owner
  is saved for their sake, and the owner's lists are evicted under every parent.
- **A token the model says the database maintains, but that it doesn't change on every write**:
  `IsRowVersion()` on SQLite without a trigger, a `[Timestamp] byte[]` on PostgreSQL instead of
  `xmin`, or a computed column that doesn't depend on the foreign key. Sluice takes the model's
  word for it, so eviction is exact but can miss a re-parent.

Foreign keys that are part of the primary key, such as a many-to-many join row's, are always
exact: a key can't change.

### Database cascades

When you delete a row, the database may delete or change rows EF never loaded: `ON DELETE
CASCADE` deletes them, and `SET NULL` sets their foreign key to null. To know which rows those
were, Sluice would have to query before every delete, which costs a round trip and can still
miss rows another writer adds in between. Instead it reads each relationship's delete behaviour
from the model. For each type the database can reach that way, following `CASCADE` to any
depth, it evicts:

- every `Entity` reader and plain query of the type;
- every `Children` reader of the foreign key the database followed to reach it. For the deleted
  row's direct children, only the lists under the deleted row are evicted;
- every `Children` reader of the type's other foreign keys too, since a deleted or nulled row
  changes every list it's in: a deleted order leaves its coupon's list of orders, and an order
  whose coupon is nulled changes in its customer's list.

`Restrict`, `NoAction`, `ClientCascade` and `ClientSetNull` never make the database touch rows
EF hasn't loaded, so they add nothing. Rows EF has loaded are saved, and invalidated, like any
other edit.

This is coarse on purpose: deleting one customer evicts every cached `Entity` read and plain
query of orders and order lines, whichever customer they belong to. Cascades the model doesn't know
about, from a trigger or a database that differs from the model, aren't seen: invalidate what
they touch by hand.

### Model shapes

| Shape | What a write invalidates |
|---|---|
| Owned types, JSON columns | the owner, as a modified row. Readers only see owned values through it |
| TPH, TPT and TPC | the base type's dependencies, so a read and a write of any type in the hierarchy see each other |
| Many-to-many join rows | the join entity, like any row, so queries through the many-to-many navigation are evicted |
| Composite keys | the row's `Children` readers and plain queries of its type (there are no `Entity` reads of it) |
| Types mapped to views | their dependencies as usual, though nothing reads them in a compute; the rest of the save is unaffected |
| Alternate keys | a foreign key to one is keyed by that key's value |
| Table splitting | the row, for every type stored in it: they share the table's dependencies, so a save or delete of one evicts the others' readers of that row and their plain queries. A foreign key or unique column of another type, in a column the save writes, is evicted whole, and deleting the row evicts what a delete of each type cascades into |

### Key folding

The database may treat two different .NET values as the same key: `1m` and `1.00m`, `"abc"` and
`"ABC "` in a case-insensitive column. So Sluice normalises (folds) key values, and the values
`read.EntityAsync` reads a unique column by, before using them: `decimal`s lose trailing zeros, strings are upper-cased without trailing spaces (for
`citext` and case-insensitive collations), and dates, durations and times of day are cut to the
microsecond. Folding can
only make an eviction broader, never miss one. Accent- and culture-sensitive collations aren't
folded.

Folding follows PostgreSQL's rules. The test suite runs on PostgreSQL, and other providers are
untested. A key, foreign key or unique column holding a date, duration or time of day the
database stores less precisely than to the microsecond, such as SQL Server's `datetime` (about
3 ms) or `smalldatetime`, MySQL's `datetime`, `timestamp` or `time` without a size (whole
seconds), a `DateTime` in a `date` column, a date in a time-of-day column (`time`, `time with
time zone`), or a precision under 6, is refused at first use, as an unmarked converter is,
below, and `[SluiceRoundTrips]` doesn't lift it: the database rounds what a save writes, so a
new row's key and a key a read asks by wouldn't match what the row holds. A type without a size
is trusted only from a provider known to keep microseconds without one (PostgreSQL's
timestamps, `interval` and `time`, SQL Server's `datetime2`, `datetimeoffset` and `time`,
SQLite's `TEXT`); on any other provider, the column type needs a size of 6 or more, such as
`datetime(6)`, which `HasPrecision(6)` writes where the type takes one. Store such a value in
SQL Server's `datetime2` or `time`, MySQL's `datetime(6)` or `time(6)`, or a PostgreSQL
`timestamp`, `interval` or `time` of precision 6 (the default), map a date-only value as
`DateOnly` and a time of day as `TimeOnly`, or key the type by another column.

A value converter on a key, foreign key or unique column must round-trip: converting a stored
value to .NET and back must give the stored value. A save reports what it would write back for
the values EF loaded, and a reader the value it asked for, so a converter that loads `"01"` as 1
and writes it back as `"1"` leaves the readers of `"01"` cached with stale data. Only the
converter's author knows whether it round-trips, so Sluice asks you to say so, once, on the
converter's class:

```csharp
user.Property(u => u.Id).HasConversion(new UserIdConverter());

// A typed id over the column's own value round-trips.
[SluiceRoundTrips]
public sealed class UserIdConverter()
    : ValueConverter<UserId, string>(id => id.Value, value => new UserId(value));
```

Without the mark, the first time Sluice uses the model (a read, `Dependency`,
`AllDependencies`, a save or a bulk write) it throws `InvalidOperationException`, naming each
such property and its converter, before anything is written. These EF built-ins give back
every value they can load, so they need no mark: `EnumToNumberConverter`, a `CastingConverter`
into a type that holds every stored value (the same type, a wider integer, any integer into
`decimal`, `float` into `double`), `DateTimeToTicksConverter` and `GuidToBytesConverter`. The rest
need it, if they round-trip for your data: the string-parsing ones (enum, number or `Guid` to
string) accept more than one spelling of a value (`"01"`, `" Active"`), a cast that narrows or
rounds loses values, `DateTimeToBinaryConverter` reads kind bits it never writes, and the bool
converters read every value but the true one as false.

- `HasConversion(id => id.Value, value => new UserId(value))` has no class to mark: move the two
  lambdas into a class deriving from `ValueConverter<TModel, TProvider>`, as above.
- A converter you don't own: derive from it and mark the derived class. The mark isn't
  inherited, since a derived converter can convert differently.
- The mark is your promise; Sluice never sees the stored value, so it can't check it.

### Rows saved without loading them

A row you attach, update or remove without loading it first has no original values, so Sluice
can't always tell which parent it had before:

| You save | Sluice invalidates |
|---|---|
| a loaded row with a [row version](#row-versions), changed through the tracker | exactly the old and new parents' `Children` readers |
| a loaded row without one, updated or deleted | every `Children` reader of each foreign key |
| `Update(row)` on an object the context didn't load | every `Children` reader of each foreign key |
| an object you built, whose required foreign key is null, or EF's sentinel for it (the CLR default, such as 0, unless `HasSentinel` says otherwise) | every `Children` reader of that foreign key |
| an object you built, of a type with a row version, whose foreign key holds any other value, or null in a nullable key | the parent it names (none, for null) |

An object you built of a type without a row version is updated or deleted like any row without
one: every `Children` reader of each foreign key. So the only miss is an object you built, with
a row version that matches the database's, whose foreign key differs from what the database
holds: the list under its real old parent isn't evicted. Unique columns follow the same rules,
with `read.EntityAsync` readers by that column in place of `Children` lists: the miss is a built
object whose unique column differs from the stored value, whose readers by the stored value
aren't evicted. Load a row before changing it, or invalidate `set.AllDependencies()` after the
save.

Don't call `Update()` or set `State = EntityState.Modified` on a row you *did* load, as generic
repositories often do. Sluice then can't tell it from an unloaded row, so every such save evicts
every `Children` list of the row's foreign keys, under every parent. Let the change tracker find
the changes. A real foreign key that equals the sentinel, such as a seeded key 0, is treated the
same way: still correct, just broader.

### Bulk updates and deletes

`ExecuteUpdate` and `ExecuteDelete` on a `UseSluice` context need no Sluice code either. Sluice
can't tell which rows a plain one changed, so once it commits it evicts every cached read of
the types stored in the table it wrote. Name the rows with `ForKey` and only their readers are
evicted; see [Bulk writes by key](#bulk-writes-by-key).

| Statement | Evicts |
|---|---|
| `ExecuteUpdate` | every read of the type, and every `Children` list of its foreign keys, since rows may have moved between parents |
| `ExecuteDelete` | the same, every `Children` list pointing at the type, and what the delete [cascades into](#database-cascades) |
| `ForKey(id).ExecuteUpdate` | that row's readers, every `Children` list of the type's foreign keys, and readers by any unique column the statement names |
| `ForKey(id).ExecuteDelete` | that row's readers, every `Children` list of the type's foreign keys, and what the delete cascades into |

They follow the same timing as saves: inside a transaction, at its commit or rollback. One that
fails or is cancelled still invalidates, as a save does, because it may have committed. One that
ran and changed no rows evicts nothing, so a scheduled job whose `ExecuteUpdate` usually matches
nothing doesn't flush the cache on every run. A delete of no rows cascades to none either.

EF doesn't tell Sluice which table a bulk statement wrote, so Sluice scans the statement's SQL
for table names. The scan can match too much, never too little:

- a table the statement only reads, in a subquery or a join, counts as written;
- a column or alias named like another table counts as that table.

Each of those evicts more than the statement changed, which costs a recompute but never serves
an old value.

### Bulk writes by key

When a bulk write is about one row, or a few, put `.ForKey(id)` or `.ForKeys(ids)` just before
the call. Sluice adds the key condition to the statement itself, alongside your `Where`, so the
statement can only touch those rows, and it evicts only their readers:

```csharp
await db.Set<FormEntity>()
    .Where(f => f.OrganisationId == organisationId && !f.IsLive && f.Status == FormStatus.Draft)
    .ForKey(id)
    .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsLive, true), ct);

await db.Set<FormEntity>().Where(f => f.OrganisationId == organisationId).ForKey(id).ExecuteDeleteAsync(ct);

await db.Set<Order>().ForKeys(ids).ExecuteUpdateAsync(s => s.SetProperty(o => o.Item, item), ct);
```

Move the key condition (`f.Id == id`) out of your `Where` and into `ForKey`; the rest of the
`Where` stays, so a conditional write is still conditional. Keep it in your `Where` as well and
the statement names the key's column twice, so an update evicts as a plain one does (see below). Sluice doesn't recognise a key
condition you write yourself: a bulk write without `ForKey` evicts every read of its type,
whatever its `Where` says. `ForKey` and `ForKeys` return a `KeyedRows<T>`, not a query: it has
`ExecuteUpdate`, `ExecuteUpdateAsync`, `ExecuteDelete` and `ExecuteDeleteAsync`, taking EF's own
setters, and nothing else, so it can't end up inside another query.

Once a keyed statement commits, Sluice evicts:

| Keyed statement | Evicts |
|---|---|
| `ExecuteUpdate` | each row's `Entity` readers, by key and by unique column (those [recorded the row they found](#by-a-unique-column)); every plain query of the type; every `Children` list of the type's foreign keys, under every parent; and every reader by a unique column whose name appears in the statement |
| `ExecuteDelete` | each row's `Entity` readers, by key and by unique column, and every plain query of the type; every `Children` list of the type's foreign keys, under every parent; the `Children` lists under each deleted row, of each foreign key that's `CASCADE` or `SET NULL`; and what the delete [cascades into](#database-cascades) |

- **Readers by a unique column.** A reader that found one of the rows is evicted through its key
  either way. One whose value no row held, a cached "not found", can only be reached through the
  column, so an update that names the column evicts every reader by it. Sluice looks for the
  column's name in the statement's SQL, as the provider quotes it, so a name that appears only in
  the `WHERE` counts too. That evicts more than it needs to, never less. So a keyed update that
  sets a form's `IsLive` leaves other forms' cached reads by slug alone, while one that sets its
  `Slug` evicts every read by slug. A delete never evicts by column: every reader that found the row
  recorded its key, and a reader that found no row still finds none.
- **`Children` lists stay whole.** A keyed update or delete still evicts every list of the type's
  foreign keys, under every parent. Sluice knows which rows changed, but not which parents they
  were under, and a list holds its rows' contents, not just their keys. Linking each list to its
  rows would add a tag per row to every cached list. A keyed delete also evicts the lists under
  each deleted row, of each foreign key that's `CASCADE` or `SET NULL` in the model (every such
  list, for a foreign key to an [alternate key](#model-shapes)).
- **Other types stay whole.** Only the type you keyed narrows. Any other type the
  [table-name scan](#bulk-updates-and-deletes) finds in the statement, such as one in a
  subquery, is evicted whole, as before.
- **More than 100 keys.** `ForKeys` with more than 100 keys evicts as a plain bulk write does,
  every read of the type, so a mass update doesn't send thousands of invalidations.
- **An update that may set the key.** EF lets `ExecuteUpdate` set the primary key, which would
  move a row to a key whose readers cached "not found". So when the statement names the key's
  column again, besides Sluice's own condition, the update evicts as a plain bulk update does.
  That includes a statement that only mentions another table's column of the same name, such as
  a `Where` through a navigation, which joins a table whose key is also `Id`: more than it needs
  to, never less.
- **One statement.** The keys apply to the statement EF runs for the call. Any other bulk
  statement on the same flow while it runs, such as one an interceptor of yours issues, or a
  retry by the execution strategy, evicts whole.

Keyed statements follow the same timing as the rest: inside a transaction, at its commit or
rollback; a failure or cancellation still invalidates; and one that changed no rows, because
your other conditions matched nothing, evicts nothing.

These throw before anything runs:

- a type with a composite primary key: `NotSupportedException`;
- a key that isn't the exact .NET type of the primary key: `ArgumentException`, naming the type
  expected, as for [`read.Entity`](#keys);
- a null key in `ForKeys`: `ArgumentException` (and a null `ForKey`, `ArgumentNullException`);
- a type not mapped to a table: `NotSupportedException`;
- a query that doesn't start from an EF set of an entity type: `ArgumentException`.

An empty `ForKeys` returns 0 and runs nothing.

## Writes Sluice can't see

Anything that writes without a `UseSluice` context goes unseen: another service, a cron job,
SQL run by hand, a database trigger, a context without `UseSluice`, `ExecuteSql` and raw
ADO.NET. After each one commits, invalidate by hand, or the old value stays cached until it
expires. Keep durations short for data written outside your app. From .NET, ask the EF set for
its dependencies:

```csharp
await db.Database.ExecuteSqlAsync($"DELETE FROM orders WHERE archived", ct);
await cache.InvalidateAsync(db.Orders.AllDependencies());
```

| Dependency | Evicts |
|---|---|
| `set.AllDependencies()` | every read of the type, and what a delete of it [cascades into](#database-cascades) |
| `set.Dependency(key)` | `Entity` readers of that row, by its key or by a unique column that found it, and plain queries of the type, but **not** `Children` readers of its parent, nor readers by a unique value no row held |

`set.Dependency(key)` can't evict `Children` readers, or a reader by a unique column whose value
no row held, because Sluice can't see the row's foreign keys or new unique values without
loading it. If something reads the rows you changed through `Children`, or by a unique value
the write may have given a row, use `set.AllDependencies()`.

EF dependencies batch with declared ones:

```csharp
await cache.InvalidateAsync([.. db.Orders.AllDependencies(), Billing.Invoice.For(invoiceId)]);
```

## When invalidation fails

Invalidating after a commit never throws: a throw there would make your code retry a write that
already committed. A failure is logged at `Error`, event id 1, under the category
`Sluice.EntityFrameworkCore.SluiceInterceptor`, and the save returns normally. The old values
then stay cached until they expire, so alert on that log entry.
