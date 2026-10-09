# EF Core

With `Sluice.EntityFrameworkCore`, you don't declare sources for your entities: Sluice works
them out from the EF model. Reads go through three helpers on `read`, and saves invalidate what
they changed by themselves once they commit. Underneath it's the same API as everywhere else:
the helpers record ordinary dependencies, and saves call `SluiceCache.InvalidateAsync`.

## Setup

```csharp
builder.Services.AddSluice();
builder.Services.AddDbContext<ShopDb>(
    (services, options) => options.UseNpgsql(connectionString).UseSluice(services)
);
```

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
- A context that calls `UseInternalServiceProvider` can't use Sluice. Strict reads need an
  interceptor shared by every context, and EF refuses one on such a context.

### Interceptor order

EF runs interceptors in the order they were added, and Sluice records what a save writes just
before EF saves it (in `SavingChanges`). Call `UseSluice` *after* adding any interceptor that
changes entities at that point, such as soft delete, auditing or an outbox, or Sluice misses
those changes:

```csharp
builder.Services.AddDbContext<ShopDb>(
    (services, options) =>
        options
            .UseNpgsql(connectionString)
            .AddInterceptors(new SoftDeleteInterceptor())
            .UseSluice(services)
);
```

Interceptors added in the context's `OnConfiguring` always run after those in the
`AddDbContext` options, so after Sluice. Add such interceptors where you call `UseSluice`
instead. The same goes for a query expression interceptor of your own: one added after
`UseSluice`, or in `OnConfiguring`, rewrites queries after Sluice has
[checked them](/guides/strict-reads).

### Pooling and factories

`AddDbContextPool((services, options) => ...)`, `AddPooledDbContextFactory` and
`AddDbContextFactory` all work. Each use of a pooled context invalidates what it commits, and
nothing an earlier use left uncommitted. Contexts sharing a transaction need nothing more, as
long as each uses `UseSluice` with the same `SluiceCache`.
See [Contexts sharing a transaction](/guides/transactions#contexts-sharing-a-transaction).

## Reading

Inside a compute:

```csharp
var customer = await read.Entity(db.Customers, customerId).SingleOrDefaultAsync(ct);

var orders = await read.Children(db.Orders, order => order.CustomerId, customerId)
    .OrderByDescending(order => order.PlacedAt)
    .Select(order => new OrderSummary(order.Id, order.Total))
    .ToListAsync(ct);

var countries = await read.All(db.Countries).ToListAsync(ct);
```

| Helper | Reads | Evicted by a write to |
|---|---|---|
| `read.Entity(set, key)` | the row with that primary key | that row |
| `read.Children(set, fk, key)` | the rows whose foreign key is `key` | any of those rows, or a row joining or leaving the set |
| `read.All(set)` | every row | any row of the type |

Each returns an `IQueryable<T>` you can filter, order, page and project. Finish it with
`Single`, `SingleOrDefault`, `ToList` and so on as usual. A read that finds nothing still
depends on what it looked for, so inserting the row later evicts the cached "not found".

### Keys

- Pass the key as the exact .NET type the model uses: the primary key's type for `Entity`, the
  foreign key property's for `Children` (nullable or not). A typed id with a value converter is
  passed as the typed id. Any other type, such as `1L` for an `int` key, throws
  `ArgumentException` naming the type expected.
- `Entity` doesn't support composite primary keys: it throws `NotSupportedException`. Use
  `Children` or `All`, or a [fused fetch](/guides/strict-reads#the-declared-route).
- `Children`'s foreign key can be the property (`o => o.CustomerId`), a shadow property named
  by a constant (`o => EF.Property<CustomerId>(o, "CustomerId")`), or a reference navigation to
  the parent (`o => o.Customer`). All three record the same dependency.

### What's refused

- **Types not mapped to a table**: keyless types, and types mapped with `ToView`, `ToFunction`
  or `ToSqlQuery`. No tracked write ever invalidates them, so the helpers throw
  `NotSupportedException`. Read them through a
  [fused fetch](/guides/strict-reads#the-declared-route) with a source you declare, and
  invalidate that by hand. An abstract TPC base type is readable when every concrete type under
  it has a table.
- **A context without `UseSluice`**: the helpers throw `InvalidOperationException`, since the
  strict-read checks wouldn't run there.
- **Reading inside a transaction**, inside a compute. See [Transactions](/guides/transactions).
- **Reading beyond the helper's own rows**: joins, navigations, `Include` and more. See
  [Strict Reads](/guides/strict-reads).
- **Skip navigations** (many-to-many without the join entity in your query). Read the join
  entity with `read.Children`.

### Cache projections or detached entities

The helpers' queries never track. A tracked query would hand back the context's own object,
with the caller's unsaved edits, and cache that for everyone. Cached entities aren't attached
to any context; to edit one, load it again on your own context. Every caller also gets the same
object, so never modify one; see
[Shared instances](/guides/configuration#shared-instances-and-auto-clone).

## Writing

Saves through EF's change tracker just work. Once a save commits, Sluice evicts the cached
reads of every row it inserted, updated or deleted, and of the lists those rows were in or
moved between. One case is broader: when a delete makes the database delete rows EF never
loaded, or set their foreign key to null (`ON DELETE CASCADE` or `SET NULL` in your model),
Sluice can't tell which rows those were. It evicts every cached read of those types instead.

In detail, once a save has committed, Sluice invalidates, for each row it added, changed or
deleted:

- the row itself: `Entity` readers of it, and `All` readers of its type;
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

- every `Entity` and `All` reader of the type;
- every `Children` reader of the foreign key the database followed to reach it. For the deleted
  row's direct children, only the lists under the deleted row are evicted;
- every `Children` reader of the type's other foreign keys too, since a deleted or nulled row
  changes every list it's in: a deleted order leaves its coupon's list of orders, and an order
  whose coupon is nulled changes in its customer's list.

`Restrict`, `NoAction`, `ClientCascade` and `ClientSetNull` never make the database touch rows
EF hasn't loaded, so they add nothing. Rows EF has loaded are saved, and invalidated, like any
other edit.

This is coarse on purpose: deleting one customer evicts every cached `Entity` and `All` read of
orders and order lines, whichever customer they belong to. Cascades the model doesn't know
about, from a trigger or a database that differs from the model, aren't seen: invalidate what
they touch by hand.

### Model shapes

| Shape | What a write invalidates |
|---|---|
| Owned types, JSON columns | the owner, as a modified row. Readers only see owned values through it |
| TPH, TPT and TPC | the base type's dependencies, so a read and a write of any type in the hierarchy see each other |
| Many-to-many join rows | the join entity's foreign keys, like any row. Reading through skip navigations isn't supported |
| Composite keys | the row's `All` and `Children` readers (there are no `Entity` reads of it) |
| Types mapped to views | their dependencies as usual, though no helper reads them; the rest of the save is unaffected |
| Alternate keys | a foreign key to one is keyed by that key's value |
| Table splitting | each type's own dependencies. Two types sharing a non-key column don't evict each other: saving one leaves the other's `Entity` and `All` readers cached |

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

## Writes Sluice can't see

Anything that writes without a `UseSluice` context goes unseen: another service, a cron job,
SQL run by hand, a database trigger, a second context without `UseSluice`, `ExecuteUpdate`,
`ExecuteDelete`, `ExecuteSql` and raw ADO.NET. After each one commits, invalidate by hand, or the
old value stays cached until it expires. Keep durations short for data written outside your
app. From .NET, ask the EF set for its dependencies:

```csharp
await db.Orders.Where(order => order.Archived).ExecuteDeleteAsync(ct);
await cache.InvalidateAsync(db.Orders.AllDependencies());
```

| Dependency | Evicts |
|---|---|
| `set.AllDependencies()` | every `Entity`, `Children` and `All` reader of the type, and what a delete of it [cascades into](#database-cascades) |
| `set.Dependency(key)` | `Entity` readers of that row and `All` readers of the type, but **not** `Children` readers of its parent |

`set.Dependency(key)` can't evict `Children` readers because Sluice can't see the row's
foreign keys without loading it. If something reads the rows you changed through `Children`,
use `set.AllDependencies()`.

Bulk-deleting parents changes their children too, and `set.AllDependencies()` covers that: after
an `ExecuteDelete` on customers, `db.Customers.AllDependencies()` also evicts readers of the
orders and order lines the database deleted, exactly as a
[tracked delete](#database-cascades) does.

EF dependencies batch with declared ones:

```csharp
await cache.InvalidateAsync([.. db.Orders.AllDependencies(), Billing.Invoice.For(invoiceId)]);
```

## When invalidation fails

Invalidating after a commit never throws: a throw there would make your code retry a write that
already committed. A failure is logged at `Error`, event id 1, under the category
`Sluice.EntityFrameworkCore.SluiceInterceptor`, and the save returns normally. The old values
then stay cached until they expire, so alert on that log entry.
