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
builder.Services.AddDbContextFactory<ShopDb>(
    (services, options) => options.UseNpgsql(connectionString).UseSluice(services)
);
```

- Register the context however you like: `AddDbContext`, `AddDbContextFactory`,
  `AddDbContextPool` or `AddPooledDbContextFactory`. Each compute opens a DI scope of its own
  and takes `ShopDb` from it, as a request does (see [Reading](#reading)); each of those
  registers `ShopDb` as a scoped service. If you register it more than one way, `UseSluice`
  runs once per context either way. Registered only through a factory class of your own (EF's
  pattern for setting a pooled context's tenant)? Register the context too, so the scope can
  make it: `services.AddScoped(sp => sp.GetRequiredService<TenantDbFactory>().CreateDbContext())`.
- A context whose constructor takes scoped services (a
  [tenant provider](/guides/strict-reads#tenants-and-other-context-state)) needs a registration
  that makes it in the scope: `AddDbContext`, or `AddDbContextFactory(..., ServiceLifetime.Scoped)`.
  The default singleton factory makes it from the root provider, which refuses scoped services
  in Development and, elsewhere, hands every context one shared instance.
- `UseSluice` needs `SluiceCache` (`AddSluice`) and logging registered. ASP.NET Core and the
  Generic Host register logging; a bare `ServiceCollection` needs `AddLogging()`. Without
  either, `UseSluice` throws `InvalidOperationException`.
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

EF runs interceptors in the order they were added, and Sluice records what a save writes just
before EF saves it (in `SavingChanges`). Call `UseSluice` *after* adding any interceptor that
changes entities at that point, such as soft delete, auditing or an outbox, or Sluice misses
those changes:

```csharp
builder.Services.AddDbContextFactory<ShopDb>(
    (services, options) =>
        options
            .UseNpgsql(connectionString)
            .AddInterceptors(new SoftDeleteInterceptor())
            .UseSluice(services)
);
```

Interceptors added in the context's `OnConfiguring` always run after those in the
registration's options, so after Sluice. Add such interceptors where you call `UseSluice`
instead. The same goes for a query expression interceptor of your own: one added after
`UseSluice`, or in `OnConfiguring`, rewrites queries after Sluice has
[worked out what they read](#what-a-query-records).

### Pooling and factories

`AddDbContextPool((services, options) => ...)`, `AddPooledDbContextFactory` and
`AddDbContextFactory` all work, for reads and writes. Each use of a pooled context invalidates
what it commits, and nothing an earlier use left uncommitted. Contexts sharing a transaction
need nothing more, as long as each uses `UseSluice` with the same `SluiceCache`. See
[Contexts sharing a transaction](/guides/transactions#contexts-sharing-a-transaction).

EF resets a pooled context's own state between uses, but not your fields, and not a
connection string you set. So what a compute's
[configure](/guides/strict-reads#tenants-and-other-context-state) sets (a tenant, a tenant's
database) is still there for the next use, which may be a request's. Set that state on every
lease, as [EF's docs do for tenants](https://learn.microsoft.com/ef/core/performance/advanced-performance-topics#managing-state-in-pooled-contexts):
a scoped factory that leases from the pool and sets the request's tenant.

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

Dependencies belong to entity types, the classes in your EF model, not to tables. Each is named
by EF's entity type name, usually its class's full name, such as `Shop.Domain.Order` (in an
inheritance hierarchy, the base class's). So a second class mapped to the same table, such as a
read model in another context, isn't linked to the first: a write through one doesn't evict
reads through the other. And moving or renaming an entity class changes its dependencies'
names. See
[Limitations](/misc/limitations#ef-core).

### Narrowing to rows

Three helpers on `read` start a query that depends on fewer rows:

```csharp
var customer = await read.Entity(computeDb.Customers, customerId).SingleOrDefaultAsync(ct);

var form = await read.Entity(computeDb.Forms, form => form.Slug, slug).SingleOrDefaultAsync(ct);

var orders = await read.Children(computeDb.Orders, order => order.CustomerId, customerId)
    .OrderByDescending(order => order.PlacedAt)
    .Select(order => new OrderSummary(order.Id, order.Total, order.Coupon!.Code))
    .ToListAsync(ct);
```

| Query | Reads | Evicted by a write to |
|---|---|---|
| `computeDb.Customers.Where(…)` | the rows its `Where` matches | any customer |
| `read.Entity(computeDb.Customers, key)` | the row with that primary key | that row |
| `read.Entity(computeDb.Forms, f => f.Slug, value)` | the row whose unique column holds `value` | that row, or a row taking or leaving `value` |
| `read.Children(computeDb.Orders, fk, key)` | the rows whose foreign key is `key` | any of those rows, or a row joining or leaving the set |

Each returns an `IQueryable<T>` you can filter, order, page and project, on the context
`read.Db` gave you. Only the helper's own type is narrowed: the `Coupon` navigation in the second
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

`read.Entity(set, column, value)` reads one row by a column that isn't its key, such as a slug
or an email, without depending on every row of the type. A save of another row doesn't evict it;
a save that gives a row that value, changes the row holding it, or moves the value off it, does.
So a cached "not found" for a slug is evicted once a row gets that slug.

The column must be covered by a single-column unique index or alternate key in the EF model
(`HasIndex(f => f.Slug).IsUnique()`). A filtered unique index counts, so a slug unique only among
the rows that have one is fine. Sluice doesn't need the uniqueness to be right; it uses the
model's unique columns to know which values a save must report. A column without one throws
`ArgumentException`, and one unique only together with others (a composite index) throws
`NotSupportedException`. Naming the primary key reads exactly as `read.Entity(set, key)`. The
value can't be null, nor one the column's value converter stores as null: no read by value
finds a null, so both throw (`ArgumentNullException`, `ArgumentException`).

Each save of a row reports one more value per unique column of its type. Without a row version
the value the database holds when an update or delete runs may not be the one EF loaded, so,
as with `Children` lists, such a save evicts every reader by that column; see
[Row versions](#row-versions). `set.Dependency(key)` doesn't evict readers by a unique column;
after a write Sluice can't see, use `set.AllDependencies()`.

A key that comes from public input, such as a slug from a URL, caches every miss. Leave misses
uncached with [`read.DoNotCache()`](/guides/sources-and-queries#leaving-a-value-uncached).

### Keys

- Pass the key as the exact .NET type the model uses: the primary key's type for `Entity`, the
  foreign key property's for `Children`. A typed id with a value converter is passed as the
  typed id. Any other type, such as `1L` for an `int` key, throws
  `ArgumentException` naming the type expected. The compiler can't check these keys: the type
  comes from the EF model.
- `Entity` doesn't support composite primary keys: it throws `NotSupportedException`. Use
  `Children`, or a plain query.
- `Children`'s foreign key property may be nullable, but the key you pass can't be null: rows
  with no parent aren't anyone's children. A null throws `ArgumentNullException`.
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
- entities that load lazily, or hold their context: select the values you need instead;
- a query on any context other than `read.Db`'s, and reading inside a transaction. See
  [Transactions](/guides/transactions);
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

Sluice reads what changed from the change tracker the same way EF does, so it never changes
what EF saves: with `AutoDetectChangesEnabled` off, an edit EF doesn't detect is neither saved
nor invalidated. A save that fails, is cancelled or hits a concurrency conflict still
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
evicts every `read.Entity` by that column, whatever the value. Inserts stay exact, and Sluice
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
| Table splitting | each type's own dependencies. Two types sharing a non-key column don't evict each other: saving one leaves the other's `Entity` readers and plain queries cached |

### Key folding

The database may treat two different .NET values as the same key: `1m` and `1.00m`, `"abc"` and
`"ABC "` in a case-insensitive column. So Sluice normalises (folds) key values, and the values
`read.Entity` reads a unique column by, before using them: `decimal`s lose trailing zeros, strings are upper-cased without trailing spaces (for
`citext` and case-insensitive collations), and dates are cut to the microsecond. Folding can
only make an eviction broader, never miss one. Accent- and culture-sensitive collations aren't
folded.

Folding follows PostgreSQL's rules. The test suite runs on PostgreSQL, and other providers are
untested. One that stores dates less precisely, such as SQL Server's `datetime` (about 3 ms),
could give the same row different key text on a read and a write, and miss an eviction.

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
with `read.Entity` readers by that column in place of `Children` lists: the miss is a built
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
can't tell which rows they changed, so once one commits it evicts every cached read of the
types stored in the table it wrote:

| Statement | Evicts |
|---|---|
| `ExecuteUpdate` | every read of the type, and every `Children` list of its foreign keys, since rows may have moved between parents |
| `ExecuteDelete` | the same, every `Children` list pointing at the type, and what the delete [cascades into](#database-cascades) |

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
| `set.Dependency(key)` | `Entity` readers of that row by its key and plain queries of the type, but **not** `Children` readers of its parent, nor `read.Entity` readers by a unique column |

`set.Dependency(key)` can't evict `Children` readers or readers by a unique column, because
Sluice can't see the row's foreign keys or unique values without loading it. If something reads
the rows you changed through `Children` or by a unique column, use `set.AllDependencies()`.

EF dependencies batch with declared ones:

```csharp
await cache.InvalidateAsync([.. db.Orders.AllDependencies(), Billing.Invoice.For(invoiceId)]);
```

## When invalidation fails

Invalidating after a commit never throws: a throw there would make your code retry a write that
already committed. A failure is logged at `Error`, event id 1, under the category
`Sluice.EntityFrameworkCore.SluiceInterceptor`, and the save returns normally. The old values
then stay cached until they expire, so alert on that log entry.
