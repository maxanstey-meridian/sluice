# What a compute can't read

Sluice only evicts a cached value when something it recorded changes. Every EF query on the
compute's own context records the entity types it reads (see
[What a query records](/guides/ef-core#what-a-query-records)). A read Sluice couldn't attribute
would record nothing, so no write would ever evict the value built from it. It would sit there
out of date, silently, until it expired.

So on a context with `UseSluice`, EF reads inside a compute are strict: anything whose reads
Sluice can't work out throws `InvalidOperationException` instead. Outside a compute nothing is
checked, and every query runs as it would without Sluice.

## What throws inside a compute

### A query whose reads Sluice can't work out

When EF compiles a query, Sluice works out what it reads and notes it in a comment at the head
of its SQL (`-- Sluice reads: …`, and `-- Sluice reads the context's state: …` when its filters
read the context). It can't for these, so they throw when they run inside a compute:

- raw SQL: `FromSql` and `SqlQuery`. SQL can read any table;
- table-valued functions, and database functions you've mapped yourself, which can read any
  table. Functions mapped with `IsBuiltIn = true`, such as `upper`, are fine;
- temporal queries (`TemporalAsOf` and the rest), which read the history table, and any other
  kind of query root EF adds that Sluice doesn't know how to follow;
- types not mapped to a table: keyless types, and types mapped with `ToView`, `ToFunction` or
  `ToSqlQuery`. No tracked write ever invalidates them;
- a query filter that reads the context's state, such as a tenant id held on the context,
  unless the compute set it with `read.Db<AppDb>(configure)`; one that reads it where the
  configure can't set it (through a service the context holds, an unmarked property with a
  body, a method, a readonly field, or a virtual, init-only or get-only property), configured or not; or one
  that reads other state that can change, such as a static tenant accessor, a captured service
  or `DateTime.Now`. See [Tenants](#tenants-and-other-context-state);
- entities that load lazily or hold their context: lazy-loading proxies, an injected lazy
  loader or `DbContext`, or an owned value that holds one. A cached one would read related rows
  later, outside the compute, or keep its context alive. They're refused even when they arrive
  through `Include` or an auto-include rather than as the query's own result. To be safe, a
  query that returns entities is refused if it reads such a type anywhere, even only in a
  filter; select the values you need instead;
- an `Include` path that isn't a constant, or `IgnoreQueryFilters` names that aren't, since
  Sluice can't tell what the query loads or which filters still apply;
- an EF command that isn't a LINQ query, so Sluice never worked out what it reads;
- a query Sluice failed to work out, which the message says as "Sluice couldn't work out what it
  reads".

With lazy-loading proxies on for the whole model (`UseLazyLoadingProxies()`), every entity type
loads lazily, so every query that returns entities is refused inside a compute. Select the
values you need (projections) instead.

The message says which, names the command by the first line of its SQL, and says what to do,
which depends on the reason:

| Refused because of | Do this instead |
|---|---|
| raw SQL, a function, a table-valued function, a temporal query or another query root Sluice doesn't know | read it in a fused fetch: [the declared route](#the-declared-route) |
| a type not mapped to a table | read it in a fused fetch |
| a query filter that reads the context's state | put the tenant in the query key and set it on the compute's context with `read.Db<AppDb>(db => db.TenantId = tenantId)`: [Tenants](#tenants-and-other-context-state) |
| a query filter that reads the context's state where the configure can't set it | copy the value into a settable field or auto-property in the constructor, filter by that, and configure it; or, for a property whose body reads a scoped service, mark it `[SluiceContextState]` and set the service with `read.Db<AppDb>((db, services) => …)`: [A tenant read through a scoped service](#a-tenant-read-through-a-scoped-service); or ignore that filter by name and filter by hand: [Tenants](#tenants-and-other-context-state) |
| a query filter that reads other state that can change | put whatever it reads in the query key, filter by it yourself, and ignore just the filters the message names, by name (`IgnoreQueryFilters(["Tenant"])`), so the others still apply. An unnamed filter can only be ignored with `IgnoreQueryFilters()`, which drops every filter, so name it first unless it's the only one: [Tenants](#tenants-and-other-context-state) |
| entities that load lazily or hold their context | select the values you need (`Select(o => new { o.Id, o.Total })`), not the entities |
| an `Include` path that isn't a constant | include by a lambda (`Include(o => o.Lines)`) or a constant path (`Include("Lines")`) |
| `IgnoreQueryFilters` names that aren't constants | name the filters by constants |
| an EF command that isn't a LINQ query | run it in a fused fetch |
| Sluice couldn't work out what it reads | run it in a fused fetch, and report it as a Sluice bug |

### A query on another context

Every EF command inside a compute must come from the context `read.Db` gave it. Your request's
context may hold tracked entities with unsaved edits, or rows it loaded before a write evicted
the value, or be inside a transaction, and the cached value is served to every caller. The read
helpers check this when you call them, too.

Use the compute's own `read`, the one its lambda was given. What you record through a `read`
lands on its compute, so one captured from another compute would record it there (or nowhere,
once that compute has returned), and this value would miss it. So `read.From`, `read.Db` and
the helpers, called on another compute's `read` inside this one, throw.

### Writes

`SaveChanges`, `ExecuteSql`, `ExecuteUpdate` and `ExecuteDelete` throw anywhere inside a compute.
A compute is a read. A write inside it would invalidate while the compute is still running,
which can throw away the very value it's building. Write before or after `GetOrSetAsync`.

### Reading inside a transaction

The compute's own context is never in your request's transaction: the compute runs outside any
ambient `TransactionScope`. A query inside a transaction the compute opens itself, or one in a
[fused fetch](#the-declared-route) on a context that's in a transaction, throws: the compute
would cache rows other callers can't see yet, which may roll back, or an old snapshot. See
[Transactions](/guides/transactions#computes-and-transactions).

### Catching doesn't help

Sluice also remembers the violation. If the compute catches the exception and carries on, the
call still throws it once the compute returns, and nothing is cached.

## The declared route

Some reads have no tables Sluice can see: a view, a keyless type, raw SQL, a function. Run them
in the fetch of a fused `read.From(dependency, fetch)`, with a source you declare:

```csharp
public static class Reports
{
    // The view of one store's sales, keyed by StoreId.
    public static readonly Source<StoreId> StoreSales = new("store-sales", id => id.Value);
}

var sales = await read.From(
    Reports.StoreSales.For(storeId),
    ct => db.StoreSales.Where(row => row.StoreId == storeId).ToListAsync(ct)
);
```

You've said what the fetch reads, so Sluice lets any EF query run inside it, on any context, and
inside tasks it starts, and records nothing more. In return, invalidate that source yourself when
the underlying data changes, after the commit:

```csharp
await db.SaveChangesAsync(ct);
await cache.InvalidateAsync(Reports.StoreSales.For(order.StoreId));
```

When what the fetch reads is stored in EF entity types, such as a view over orders and stores
or a Dapper query of their tables, declare those types' dependencies instead, and there's
nothing to invalidate yourself:

```csharp
var sales = await read.From(
    [.. db.Orders.AllDependencies(), .. db.Stores.AllDependencies()],
    ct => db.StoreSales.Where(row => row.StoreId == storeId).ToListAsync(ct)
);
```

Declared this way, the value depends on every row of each type and every list of its rows, as
a plain query of the type does. So once any save, `ExecuteUpdate` or `ExecuteDelete` of an order
or a store commits on a `UseSluice` context, the value is evicted. It's broad: any order evicts
it, not only that store's, and so does a write to any type a delete of orders or stores
[cascades into](/guides/ef-core#database-cascades), such as order lines. Writes Sluice can't see
still need [invalidating by hand](/guides/ef-core#writes-sluice-can-t-see).

Inside a fetch, writes still throw, nesting still throws, and so do reads inside a transaction.
A query in a fetch on your own context can track, so add `AsNoTracking()` yourself.
`read.From(dependency)` without a fetch allows nothing extra.

## What isn't checked

- **Reads that run no SQL on another context.** If a compute captures your request's context
  and calls `Find` on an entity it already tracks, or reads `Local` or a navigation that's
  already loaded, no command runs, so nothing sees it: the context hands back what it holds,
  unsaved edits included. Query `read.Db`'s context instead.
- **Raw ADO.NET or Dapper on a context's connection**, `read.Db`'s included. They aren't EF
  commands, so outside a fetch they record nothing. Run them in a
  [fused fetch](#the-declared-route).
- **A context without `UseSluice`.** Its queries aren't seen at all. A compute that reads only
  through one records nothing, and throws for that instead.
- **Work started under `ExecutionContext.SuppressFlow`.** It can't see the compute, so every
  guard is off there.
- **An entity that loads lazily, wrapped in your own type** (a DTO or record holding it). The
  check only looks at entities a query returns directly, and in anonymous types, tuples and
  arrays.
- **A query in a fused fetch that reads rows its dependency doesn't cover.** You declared what
  the fetch reads; Sluice takes your word for it.
- **A query expression interceptor of your own added after `UseSluice`**, or in
  `OnConfiguring`. It rewrites queries after Sluice has worked out what they read.
- **A database function mapped with `IsBuiltIn = true`.** It's trusted not to read other tables.

## Tenants and other context state

A query filter that reads a field or property on the context
(`HasQueryFilter(i => i.TenantId == TenantId)`) gives each context different rows, but a cached
value is served to every caller with its key. So inside a compute it only runs on a context the
compute set up itself, from the key: pass a configure to the compute's first `read.Db`.

```csharp
// A struct, so the filter reads a plain value; a class here (a record class) would be refused.
public readonly record struct TenantId(string Value);

// On AppDb, settable, so the configure can set it:
//     public TenantId TenantId { get; set; }
//     model.Entity<Invoice>().HasQueryFilter("Tenant", invoice => invoice.TenantId == TenantId);

public static readonly Query<TenantId, IReadOnlyList<InvoiceSummary>> TenantInvoices = new(
    "invoice-summaries",
    id => id.Value
);

public ValueTask<IReadOnlyList<InvoiceSummary>> GetInvoices(
    TenantId tenantId,
    CancellationToken ct
) =>
    cache.GetOrSetAsync(
        TenantInvoices,
        tenantId,
        async Task<IReadOnlyList<InvoiceSummary>> (read, ct) =>
            await read.Db<AppDb>(db => db.TenantId = tenantId)
                .Invoices.Select(invoice => new InvoiceSummary(invoice.Id, invoice.Total))
                .ToListAsync(ct),
        ct
    );
```

The configure runs once, when the compute's context is made, before anything can query it.
Later calls in the compute take no configure (`read.Db<AppDb>()`) and get the same context; a
configure on a later call throws, since queries may already have run without it. Without a
configure, a query whose filters read the context's state throws, saying so.

Sluice takes three things on trust here, as it takes a fused fetch's dependencies:

- **The configure sets every value the filters read.** Sluice can't see what it sets. One that
  only sets a timeout leaves whatever the context's constructor put there.
- **The values come from the key.** `db.TenantId = tenantId`, where `tenantId` is the key, is
  right; `db.TenantId = tenantProvider.TenantId` reads the request, not the key.
- **The configure overwrites what the constructor set.** The compute's context is made in a DI
  scope of the compute's own, so it can take scoped services, but it's made on the async
  context (`AsyncLocal`, `IHttpContextAccessor`) of whichever caller computes first. A tenant
  provider over `IHttpContextAccessor` gives that caller's tenant, and the configure must
  overwrite it.

What Sluice does check: the filter must read the state where it's stored and where the
configure can set it: a field that isn't readonly, or an auto-property that isn't virtual and has
a setter that isn't `init`, of the context, holding a plain value (a string, number, `Guid`, enum or a struct of
those). A filter that reads it through a tenant service the context holds
(`i.TenantId == _tenants.TenantId`), an unmarked property with a body, a method, a `readonly` field, a
virtual property, or a `{ get; }` or `{ get; init; }` property is refused even on a configured context, since the
configure can't set it. Copy the value into a settable field or property in the constructor, and
filter by that, or mark the property, below.

### A tenant read through a scoped service

Most ASP.NET apps don't copy the tenant onto the context: the filter reads it through a property
over the request's scoped tenant service, which middleware sets once the user is known, after
the context was made.

```csharp
public sealed class AppDb(DbContextOptions<AppDb> options, TenantProvider tenant)
    : DbContext(options)
{
    // The body reads only what the compute's configure sets, so Sluice may trust it.
    [SluiceContextState]
    private Guid? TenantId => tenant.IsSet ? tenant.Id : null;

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<Invoice>().HasQueryFilter("Tenant", invoice => invoice.TenantId == TenantId);
}
```

The configure then sets the service, not the context. Its second argument is the compute's own
DI scope. On a context `AddDbContext` or a scoped factory makes, that's the scope the context
was made in, so the service it gets is the one the context holds:

```csharp
read.Db<AppDb>((db, services) => services.GetRequiredService<TenantProvider>().Set(tenantId))
```

`[SluiceContextState]` is your promise: "this property's body reads only state that `read.Db`'s
configure sets". Sluice doesn't read the body. A marked property is treated exactly like a
settable field: the filter runs only on a configured context, and its queries carry the
`-- Sluice reads the context's state: …` comment. If the body reads anything else (the clock, a
static, a value the configure didn't set), one caller's rows are cached for everyone with that
key. The property must hold a plain value, must have a body and mustn't be virtual or abstract,
though an interface's implementation is fine; it may be private. The mark does nothing on an
auto-property: a settable one is already accepted, and a get-only or init-only one is still
refused, since it holds only what the constructor set. Unmarked properties with a body, methods, and members of objects the context holds
(`_tenants.TenantId` written straight into the filter) are still refused, and the refusal of an
unmarked property says how to mark it.

When the scoped service carries the user as well as the tenant (their id, their role), setting
it in the compute's scope would make the compute run as a user who doesn't exist, and anything
else in that scope that reads the user would see that one. Give the context a tenant of its own
for the configure to set instead, and read it first:

```csharp
public sealed class AppDb(DbContextOptions<AppDb> options, UserContext user) : DbContext(options)
{
    private Guid? _readAsTenantId;

    // Only a compute's configure calls this, never a request.
    public void ReadAsTenant(Guid tenantId) => _readAsTenantId = tenantId;

    // The compute's tenant wins over any user; a request's context never sets it.
    [SluiceContextState]
    private Guid? TenantId =>
        _readAsTenantId ?? (user.IsAuthenticated ? user.TenantId : null);
}
```

```csharp
read.Db<AppDb>(db => db.ReadAsTenant(tenantId))
```

The configure's tenant must come first. A user service that reads `IHttpContextAccessor` gives
the compute the user of whichever caller computes first (see above), and with the user first,
that user's tenant would be cached for everyone with the key. On a request's context the field
is never set, so the user's tenant applies as before. Call `ReadAsTenant` only from a compute's
configure: on a request's context it would override the user's tenant. With a pooled context
the field is still set for the next lease, which may be a request's, so clear it on every lease
in the scoped factory, or don't pool this context.

A context leased straight from `AddDbContextPool` was made by the pool, not the compute's scope,
and keeps whatever service an earlier lease gave it, so the configure would set a different
object from the one the property reads. Lease it through a scoped factory that hands it the
scope's service on every lease, as EF documents for pooled tenants, or point the context at the
scope's service in the configure as well.

The configure can also point the context at a tenant's own database before its first query,
for database-per-tenant:

```csharp
read.Db<AppDb>(db => db.Database.SetConnectionString(tenants.ConnectionStringOf(tenantId)))
```

Dependencies are per entity type and row, not per tenant or database, so a write in one tenant
evicts other tenants' cached reads of the same type (or, through `read.Entity`, the same key).
That's safe, just broader than it needs to be.

With a pooled context, what the configure set stays on the context for its next use; see
[Pooling and factories](/guides/ef-core#pooling-and-factories).

When the tenant id is a foreign key to a tenants table, read through `read.Children` to narrow
that: `read.Children(read.Db<AppDb>(db => db.TenantId = tenantId).Invoices, invoice =>
invoice.TenantId, tenantId)` records one dependency on that tenant's invoices, so only their
writes evict it, and the filter still applies.

You can also filter by hand instead: ignore the tenant filter by name
(`IgnoreQueryFilters(["Tenant"])`) and add your own `Where`, with the tenant in the key. Forget
the `Where` once, though, and one tenant's rows are served to another; the filter doesn't
forget. Don't reach for `IgnoreQueryFilters()` without names: it drops every query filter in
the query, on every type it reads, so soft-deleted rows would land in the cached value. An
unnamed filter (`HasQueryFilter(i => …)`) can only be ignored that way, so name it unless it's
the only filter the query applies:

```csharp
// In OnModelCreating. Named filters can be ignored one at a time.
model.Entity<Invoice>().HasQueryFilter("Tenant", invoice => invoice.TenantId == TenantId);
model.Entity<Invoice>().HasQueryFilter("SoftDelete", invoice => !invoice.IsDeleted);
```

A filter that reads anything outside the rows that can change is refused too, for the same
reason: a static tenant accessor (`HasQueryFilter(i => i.TenantId == TenantContext.Current)`), a
method or field of a service captured where the filter was configured, a database function that
reads the connection's settings (`current_setting`) or the clock, and `DateTime.Now`. Each can
give a different value on each call, but the cached value is served whatever it is now. Put the
value in the query key, ignore that filter by name and filter by it yourself (the message names
each refused filter and what to pass); for time, put a rounded date in the key, such as today's,
and filter by that. What a filter may read besides the rows: constants, `static
readonly` fields and static `{ get; } = …` properties holding a plain value (a number, string,
date, `Guid` or enum), and plain local values captured where the filter was written. Calling a
method, or constructing a value of your own type (`new UserId(5)`), is refused even when it
never changes, since Sluice can't see inside it.
