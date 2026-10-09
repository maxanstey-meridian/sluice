# EF Core

`Sluice.EntityFrameworkCore` takes dependencies from the EF model instead of declarations.
Reads go through three helpers on `read`, and tracked writes invalidate on their own once they
commit. Underneath, it's the core API: the helpers record ordinary dependencies, and the
writes go through `SluiceCache.InvalidateAsync`.

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
- A context that calls `UseInternalServiceProvider` can't use Sluice: EF refuses, on such a
  context, the singleton interceptor Sluice's strict reads need.

### Interceptor order

EF runs interceptors in the order they were added, and Sluice records what a save writes in its
own `SavingChanges`. Call `UseSluice` *after* adding any interceptor that changes entities there,
such as soft delete, auditing or an outbox, or Sluice misses those changes:

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
`AddDbContextFactory` all work. Each lease invalidates what it commits, and nothing an earlier
lease left uncommitted. Every context on one `SluiceCache` shares one interceptor, so contexts
sharing a transaction need nothing more, as long as each uses `UseSluice` with the same cache.
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

Each returns an `IQueryable<T>` to compose: filter, order, page, project. Choose `Single`,
`SingleOrDefault`, `ToList` and so on yourself. A read that finds nothing still depends on what
it looked for, so inserting the row later evicts the "not found" value.

### Keys

- `Entity` needs the primary key's CLR type exactly, and `Children` the foreign key property's
  (nullability ignored). A strongly typed id with a value converter is passed as the id. A key
  of another type, such as `1L` for an `int` key, throws `ArgumentException` naming the
  expected type.
- `Entity` doesn't support composite primary keys: it throws `NotSupportedException`. Use
  `Children` or `All`, or a [fused fetch](/guides/strict-reads#the-declared-route).
- `Children`'s foreign key can be the property (`o => o.CustomerId`), a shadow property named
  by a constant (`o => EF.Property<CustomerId>(o, "CustomerId")`), or a reference navigation to
  the parent (`o => o.Customer`). All three record the same dependency.

### What's refused

- **Types not mapped to a table**: keyless types, and types mapped with `ToView`, `ToFunction`
  or `ToSqlQuery`. No tracked write ever invalidates them, so the helpers throw
  `NotSupportedException`. Read them through a
  [fused fetch](/guides/strict-reads#the-declared-route) with a declared source, and invalidate
  that by hand. An abstract TPC root is readable when every concrete type under it has a table.
- **A context without `UseSluice`**: the helpers throw `InvalidOperationException`, since the
  strict checks wouldn't run there.
- **Running inside a transaction**, inside a compute. See [Transactions](/guides/transactions).
- **Reading beyond the helper's own rows**: joins, navigations, `Include` and more. See
  [Strict Reads](/guides/strict-reads).
- **Skip navigations** (many-to-many without the join entity in your query). Read the join
  entity with `read.Children`.

### Cache projections or detached entities

The helpers' queries never track. A tracked query would hand back the context's own instance,
with the caller's unsaved edits, and cache that for everyone. Cached entities are detached; to
edit one, load it again on your own context. They're also shared by every caller, so never
change one in place; see
[Shared instances](/guides/configuration#shared-instances-and-auto-clone).

## Writing

Tracked saves just work. Inserts, updates and deletes evict exactly the rows they wrote and the
foreign keys those rows had before and after. When a delete lets the database remove or null
rows EF never loaded (an `ON DELETE CASCADE` or `SET NULL` in your model), Sluice can't see
which rows those were, so it evicts every cached read of the affected types that could include
them instead.

In detail, once a save has committed, Sluice invalidates, for each row it added, changed or
deleted:

- the row's own key: its `Entity` readers, and `All` readers of its type;
- each foreign key's value before and after the save: `Children` readers of the old parent and
  the new one (nulls are skipped, so setting a key to or from null evicts the one parent it had);
- for a deleted row, every foreign key that points at it: `Children` readers of its own
  children;
- for a deleted row, what the database does to other types: see
  [Database cascades](#database-cascades).

`Children` readers are evicted by *any* change to a member, not only by rows joining or leaving.
One dependency covers the whole list; that's coarse on purpose.

Sluice reads what changed from the change tracker the way EF does, so it never changes what EF
saves: with `AutoDetectChangesEnabled` off, an edit EF doesn't detect isn't saved or invalidated.
A save that fails, is cancelled or hits a concurrency conflict still invalidates what it
captured, because an earlier batch may have committed.

### Database cascades

When you delete a row, the database may delete or change rows EF never loaded: `ON DELETE
CASCADE` deletes them, and `SET NULL` blanks their foreign key. Sluice can't see which rows
those were without a query before every delete, which would cost a round trip and race other
writers. So it reads each relationship's delete behaviour from the model and, for each type the
database can reach that way, through `CASCADE` at any depth, evicts:

- every `Entity` and `All` reader of the type;
- every `Children` reader of the foreign key the database followed to reach it. At the first
  step the deleted row's key evicts exactly its own children's lists instead;
- every `Children` reader of the type's other foreign keys too, since a deleted or nulled row
  changes in every list it's in: a deleted order leaves its coupon's list of orders, and an
  order whose coupon is nulled changes in its customer's list.

`Restrict`, `NoAction`, `ClientCascade` and `ClientSetNull` leave rows EF hasn't loaded alone in
the database, so they add nothing. Rows EF has loaded are saved, and captured, like any edit.

This is coarse on purpose: deleting one customer evicts every cached `Entity` and `All` read of
orders and order lines, whichever customer they belong to. Cascades the model doesn't know about,
from a trigger or a schema that drifted from the model, aren't seen: invalidate the sets they
touch by hand.

### Model shapes

| Shape | What a write invalidates |
|---|---|
| Owned types, JSON columns | the owner, as a modified row. Readers only see owned values through it |
| TPH, TPT and TPC | the root type's dependencies, so reads and writes of any derived type meet |
| Many-to-many join rows | the join entity's foreign keys, like any row. Reading through skip navigations isn't supported |
| Composite keys | the row's `All` and `Children` readers (there are no `Entity` reads of it) |
| Types mapped to views | their dependencies as usual, though no helper reads them; the rest of the save is unaffected |
| Alternate keys | a foreign key to one is keyed by that key's value |
| Table splitting | each type's own dependencies. Two types sharing a non-key column don't evict each other: saving one leaves the other's `Entity` and `All` readers cached |

### Key folding

Key values are folded, so values the database treats as equal give one dependency: `decimal`
scale (`1m` and `1.00m`), strings by upper case without trailing spaces (for `citext` and
case-insensitive collations), and dates to the microsecond. Folding only ever over-invalidates.
Accent- and culture-sensitive collations aren't folded.

Folding follows PostgreSQL's comparison rules: microsecond date-times, and case-insensitive or
blank-padded strings. The test suite runs on PostgreSQL, and other providers are untested. One
that stores dates less precisely, such as SQL Server's `datetime` (about 3 ms), could give a
date-keyed read and write different text, and miss an eviction.

### Rows saved without loading them

A row you attach, update or remove without loading it first has no original values, so Sluice
can't always tell which parent it had:

| You save | Sluice invalidates |
|---|---|
| a loaded row, changed through the tracker | exactly the old and new parents' `Children` readers |
| `Update(row)` on an object the context didn't load | every `Children` reader of each foreign key |
| an object you built, whose required foreign key is null, or EF's sentinel for it (the CLR default, such as 0, unless `HasSentinel` says otherwise) | every `Children` reader of that foreign key |
| an object you built, whose foreign key holds any other value, or null in a nullable key | the parent it names (none, for null) |

So a row saved from an object you built is only missed when its foreign key is set to a value
that differs from what the database holds: then the real old parent's `Children` readers are
missed. Load a row before changing it, or invalidate `set.AllDependencies()` after the save.

Don't call `Update()` or set `State = EntityState.Modified` on a row you *did* load, as generic
repositories often do. It looks the same as an update of an unloaded row, so every such save
evicts every `Children` reader of the row's foreign keys, for every parent. Let the change
tracker find the changes. A real foreign key equal to the sentinel, such as a seeded key 0, is
taken for a stub's in the same way: correct, but coarse.

## Writes Sluice can't see

Anything that writes without a `UseSluice` context goes unseen: another service, a cron job,
SQL run by hand, a database trigger, a second context without `UseSluice`, `ExecuteUpdate`,
`ExecuteDelete`, `ExecuteSql` and raw ADO.NET. Each must invalidate by hand, after it commits,
or the old value stays cached until it expires. Keep durations short for data written outside
your app. From .NET, use the dependencies the EF package derives:

```csharp
await db.Orders.Where(order => order.Archived).ExecuteDeleteAsync(ct);
await cache.InvalidateAsync(db.Orders.AllDependencies());
```

| Dependency | Evicts |
|---|---|
| `set.AllDependencies()` | every `Entity`, `Children` and `All` reader of the type, and what a delete of it [cascades into](#database-cascades) |
| `set.Dependency(key)` | `Entity` readers of that row and `All` readers of the type, but **not** `Children` readers of its parent |

`set.Dependency(key)` can't evict `Children` readers because Sluice can't see the row's
foreign keys without loading it. After a bypass that changed rows something reads through
`Children`, use `set.AllDependencies()`.

Bulk-deleting parents changes their children too, and `set.AllDependencies()` covers that: after
an `ExecuteDelete` on customers, `db.Customers.AllDependencies()` also evicts readers of the
orders and order lines the database deleted, with the same gaps as a
[tracked delete](#database-cascades).

EF dependencies batch with declared ones:

```csharp
await cache.InvalidateAsync([.. db.Orders.AllDependencies(), Billing.Invoice.For(invoiceId)]);
```

## When invalidation fails

Invalidating after a commit never throws: a throw there would make your code retry a write that
already committed. A failure is logged at `Error`, event id 1, under the category
`Sluice.EntityFrameworkCore.SluiceInterceptor`, and the save returns normally. The stale
values live until their duration ends, so alert on that log entry.
