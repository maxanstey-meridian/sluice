# EF Core

With `Sluice.EntityFrameworkCore`, you don't declare sources for your entities: Sluice works
them out from the EF model. Inside a compute you query a context of the compute's own with
ordinary LINQ, and Sluice records every table each query reads. Saves and bulk updates
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

- Register the context with `AddDbContextFactory` or `AddPooledDbContextFactory`. Each compute
  gets a context of its own from the factory (see [Reading](#reading)), and the factory also
  registers `ShopDb` itself, so the rest of your code injects it as before. If you register the
  context with `AddDbContext` or `AddDbContextPool` too, `UseSluice` runs once per context
  either way.
- `UseSluice` needs `SluiceCache` (`AddSluice`) and logging registered. ASP.NET Core and the
  Generic Host register logging; a bare `ServiceCollection` needs `AddLogging()`. Without
  either, `UseSluice` throws `InvalidOperationException`.
- Relational providers only: Sluice reads each save's transaction from the relational
  connection. A context on any other provider throws `NotSupportedException` when it saves,
  before it writes anything, and the read helpers refuse it. The test suite runs on
  PostgreSQL; see [Key folding](#key-folding) for what that means for other providers.
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
`AddDbContextFactory` all work for writes. Each use of a pooled context invalidates what it
commits, and nothing an earlier use left uncommitted. `read.Db` needs one of the two factories;
with `AddDbContextPool`, register a factory beside it. Contexts sharing a transaction need
nothing more, as long as each uses `UseSluice` with the same `SluiceCache`. See
[Contexts sharing a transaction](/guides/transactions#contexts-sharing-a-transaction).

## Reading

Inside a compute, `read.Db<ShopDb>()` gives you the compute's own context. Query it with
ordinary LINQ:

```csharp
var db = read.Db<ShopDb>();

var customer = await db.Customers
    .Include(customer => customer.Address)
    .SingleOrDefaultAsync(customer => customer.Id == customerId, ct);

var orders = await db.Orders
    .Where(order => order.CustomerId == customerId)
    .OrderByDescending(order => order.PlacedAt)
    .Select(order => new OrderSummary(order.Id, order.Total, order.Coupon!.Code))
    .ToListAsync(ct);
```

The context is made from your factory the first time the compute asks for it, and the same one
comes back for every call in that compute. It's disposed when the compute ends. It holds nothing
from outside the compute, so it can't hand back your request's tracked entities or their unsaved
edits. Its queries don't track, and it isn't in your request's transaction, so it reads what's
committed.

### What a query records

Each query records every table it reads, and a write to any of them evicts the value. Sluice
works that out from the query when EF compiles it, so everything EF can express counts:

- the sets it starts from, and those it joins, unions or reads in a subquery;
- every navigation it follows, in a filter, a projection or an `Include`, by property, through
  `EF.Property`, through a cast or an interface, or by name;
- many-to-many navigations, which read the join table too;
- what EF adds by itself: [auto-included](https://learn.microsoft.com/ef/core/querying/related-data/eager#model-configuration-for-auto-including-navigations)
  navigations, unless you call `IgnoreAutoIncludes()`, and each type's query filters, unless you
  call `IgnoreQueryFilters()`;
- `Find`, explicit loading (`Entry(order).Reference(o => o.Customer).LoadAsync()`) and compiled
  queries.

Owned types and JSON columns are stored in their owner's row, so they count as the owner.

A query records the whole of each table, so in the example above, a change to *any* customer,
address, order or coupon evicts the value (if `Address` were an owned type, it would count as the
customer). That's always correct, just broad. When it's too broad, narrow it.
So you can see which queries do this, Sluice logs each one the first time it runs, at
`Information`, naming the query and the types it reads whole (see
[Logging](/guides/configuration#logging)).

### Narrowing to rows

Two helpers on `read` start a query that depends on fewer rows:

```csharp
var customer = await read.Entity(db.Customers, customerId).SingleOrDefaultAsync(ct);

var orders = await read.Children(db.Orders, order => order.CustomerId, customerId)
    .OrderByDescending(order => order.PlacedAt)
    .Select(order => new OrderSummary(order.Id, order.Total, order.Coupon!.Code))
    .ToListAsync(ct);
```

| Query | Reads | Evicted by a write to |
|---|---|---|
| `db.Customers.Where(…)` | the rows its `Where` matches | any customer |
| `read.Entity(db.Customers, key)` | the row with that primary key | that row |
| `read.Children(db.Orders, fk, key)` | the rows whose foreign key is `key` | any of those rows, or a row joining or leaving the set |

Each returns an `IQueryable<T>` you can filter, order, page and project, on the context
`read.Db` gave you. Only the helper's own type is narrowed: the `Coupon` navigation in the second
query still depends on every coupon. A read that finds nothing still depends on what it looked
for, so inserting the row later evicts the cached "not found". The helpers never track and don't
load auto-included navigations.

A helper's query skips auto-includes by calling `IgnoreAutoIncludes()`, and EF applies that to
the whole query, not just the helper's set. So a plain query combined with a helper's, in a
join, a subquery or a `Concat`, loses its own auto-includes too. Add an `Include` for each
navigation you need there.

### Keys

- Pass the key as the exact .NET type the model uses: the primary key's type for `Entity`, the
  foreign key property's for `Children` (nullable or not). A typed id with a value converter is
  passed as the typed id. Any other type, such as `1L` for an `int` key, throws
  `ArgumentException` naming the type expected. The compiler can't check these keys: the type
  comes from the EF model.
- `Entity` doesn't support composite primary keys: it throws `NotSupportedException`. Use
  `Children`, or a plain query.
- `Children`'s foreign key can be the property (`o => o.CustomerId`), a shadow property named
  by a constant (`o => EF.Property<CustomerId>(o, "CustomerId")`), or a reference navigation to
  the parent (`o => o.Customer`). All three record the same dependency.

### What's refused

Inside a compute, these throw `InvalidOperationException`. Each would cache a value that no
write evicts, or one built from data other callers shouldn't see.
[Strict Reads](/guides/strict-reads) says why for each, and how to read them anyway:

- raw SQL (`FromSql`, `SqlQuery`), table-valued functions and database functions of your own;
- types not mapped to a table: keyless types, and types mapped with `ToView`, `ToFunction` or
  `ToSqlQuery`. No tracked write ever invalidates them. An abstract TPC base type is fine when
  every concrete type under it has a table;
- a query filter that reads the context's state, such as a tenant id;
- entities that load lazily, or hold their context: select the values you need instead;
- a query on any context other than `read.Db`'s, and reading inside a transaction. See
  [Transactions](/guides/transactions);
- writes.

The helpers also refuse types not mapped to a table, with `NotSupportedException`, and a
context without `UseSluice`.

### Cache projections or detached entities

Cached entities aren't attached to any context; to edit one, load it again on your own context.
Every caller also gets the same object, so never modify one; see
[Shared instances](/guides/configuration#shared-instances-and-auto-clone).

## Writing

Saves through EF's change tracker just work. Once a save commits, Sluice evicts the cached
reads of every row it inserted, updated or deleted, and of the lists those rows were in or
moved between, and every plain query of those tables. [Bulk updates](#bulk-updates-and-deletes)
work too. One case is broader: when a delete makes the database delete rows EF never
loaded, or set their foreign key to null (`ON DELETE CASCADE` or `SET NULL` in your model),
Sluice can't tell which rows those were. It evicts every cached read of those types instead.

In detail, once a save has committed, Sluice invalidates, for each row it added, changed or
deleted:

- the row itself: `Entity` readers of it, and every plain query of its type;
- the parent it had before the save and the one it has after: `Children` readers of each. A
  null foreign key has no parent, so setting a key to or from null evicts just the one;
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
`"ABC "` in a case-insensitive column. So Sluice normalises (folds) key values before using
them: `decimal`s lose trailing zeros, strings are upper-cased without trailing spaces (for
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
| a loaded row, changed through the tracker | exactly the old and new parents' `Children` readers |
| `Update(row)` on an object the context didn't load | every `Children` reader of each foreign key |
| an object you built, whose required foreign key is null, or EF's sentinel for it (the CLR default, such as 0, unless `HasSentinel` says otherwise) | every `Children` reader of that foreign key |
| an object you built, whose foreign key holds any other value, or null in a nullable key | the parent it names (none, for null) |

So the only miss is an object you built whose foreign key differs from what the database
holds: the list under its real old parent isn't evicted. Load a row before changing it, or
invalidate `set.AllDependencies()` after the save.

Don't call `Update()` or set `State = EntityState.Modified` on a row you *did* load, as generic
repositories often do. Sluice then can't tell it from an unloaded row, so every such save evicts
every `Children` list of the row's foreign keys, under every parent. Let the change tracker find
the changes. A real foreign key that equals the sentinel, such as a seeded key 0, is treated the
same way: still correct, just broader.

### Bulk updates and deletes

`ExecuteUpdate` and `ExecuteDelete` on a `UseSluice` context need no Sluice code either. Sluice
can't tell which rows they changed, so once one commits it evicts every cached read of the
table it wrote:

| Statement | Evicts |
|---|---|
| `ExecuteUpdate` | every read of the type, and every `Children` list of its foreign keys, since rows may have moved between parents |
| `ExecuteDelete` | the same, every `Children` list pointing at the type, and what the delete [cascades into](#database-cascades) |

They follow the same timing as saves: inside a transaction, at its commit or rollback. One that
fails or is cancelled still invalidates, as a save does, because it may have committed.

EF doesn't tell Sluice which table a bulk statement wrote, so Sluice scans the statement's SQL
for table names. The scan can match too much, never too little:

- a table the statement only reads, in a subquery or a join, counts as written;
- a column or alias named like another table counts as that table;
- an unquoted, lowercase table name counts wherever it appears inside a longer name, so
  `orders` matches in `orders_archive`.

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
| `set.Dependency(key)` | `Entity` readers of that row and plain queries of the type, but **not** `Children` readers of its parent |

`set.Dependency(key)` can't evict `Children` readers because Sluice can't see the row's
foreign keys without loading it. If something reads the rows you changed through `Children`,
use `set.AllDependencies()`.

EF dependencies batch with declared ones:

```csharp
await cache.InvalidateAsync([.. db.Orders.AllDependencies(), Billing.Invoice.For(invoiceId)]);
```

## When invalidation fails

Invalidating after a commit never throws: a throw there would make your code retry a write that
already committed. A failure is logged at `Error`, event id 1, under the category
`Sluice.EntityFrameworkCore.SluiceInterceptor`, and the save returns normally. The old values
then stay cached until they expire, so alert on that log entry.
