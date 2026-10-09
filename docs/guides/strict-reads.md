# Strict Reads

Sluice only evicts a cached value when something it recorded changes. Every EF query on the
compute's own context records the tables it reads (see
[What a query records](/guides/ef-core#what-a-query-records)). A read Sluice couldn't attribute
would record nothing, so no write would ever evict the value built from it. It would sit there
out of date, silently, until it expired.

So on a context with `UseSluice`, EF reads inside a compute are strict: anything whose reads
Sluice can't work out throws `InvalidOperationException` instead. Outside a compute nothing is
checked, and every query runs as it would without Sluice.

## What throws inside a compute

### A query whose reads Sluice can't work out

When EF compiles a query, Sluice works out what it reads and notes it in a comment at the head
of its SQL (`-- Sluice reads: …`). It can't for these, so they throw when they run inside a
compute:

- raw SQL: `FromSql` and `SqlQuery`. SQL can read any table;
- table-valued functions, and database functions you've mapped yourself, which can read any
  table. Functions mapped with `IsBuiltIn = true`, such as `upper`, are fine;
- temporal queries (`TemporalAsOf` and the rest), which read the history table, and any other
  kind of query root EF adds that Sluice doesn't know how to follow;
- types not mapped to a table: keyless types, and types mapped with `ToView`, `ToFunction` or
  `ToSqlQuery`. No tracked write ever invalidates them;
- a query filter that reads the context's state, such as a tenant id held on the context, or
  static state, such as a static tenant accessor or `DateTime.Now`. See
  [Tenants](#tenants-and-other-context-state);
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
| a query filter that reads the context's state or static state | put the tenant (or whatever it reads) in the query key, filter by it yourself and add `IgnoreQueryFilters()`: [Tenants](#tenants-and-other-context-state) |
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
once that compute has returned), and this value would miss it. So `read.From`, `read.Db` and the helpers, called on
another compute's `read` inside this one, throw.

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

Inside a fetch, writes still throw, nesting still throws, and so do reads inside a transaction.
A query in a fetch on your own context can track, so add `AsNoTracking()` yourself.
`read.From(dependency)` without a fetch allows nothing extra.

## What isn't checked

- **Reads that run no SQL on another context.** If a compute captures your request's context
  and calls `Find` on an entity it already tracks, or reads `Local` or a navigation that's
  already loaded, no command runs, so nothing sees it: the context hands back what it holds,
  unsaved edits included. Query `read.Db`'s context instead.
- **Raw ADO.NET or Dapper on a context's connection.** They aren't EF commands.
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

A query filter that reads a field on the context (`HasQueryFilter(i => i.TenantId == _tenantId)`)
gives each context different rows, but a cached value is served to every context. The compute's
own context comes from the factory, so it doesn't have your request's tenant anyway. So a query
that gets such a filter throws inside a compute. Put the tenant in the query key and filter by it
yourself:

```csharp
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
            await read.Children(read.Db<AppDb>().Invoices, invoice => invoice.TenantId, tenantId)
                .IgnoreQueryFilters()
                .Select(invoice => new InvoiceSummary(invoice.Id, invoice.Total))
                .ToListAsync(ct),
        ct
    );
```

Here `TenantId` is a foreign key to a tenants table, so `Children` records one dependency per
tenant. Without such a key, `read.Db<AppDb>().Invoices.IgnoreQueryFilters().Where(...)` works
too, but more broadly: a write to any invoice, in any tenant, evicts it.

The same goes for any context-dependent filter, such as soft delete toggled per context:
whatever changes what a compute reads must be part of the query key.

A filter that reads static state is refused too, for the same reason: a static tenant accessor
(`HasQueryFilter(i => i.TenantId == TenantContext.Current)`) gives a different value on each
call, and `DateTime.Now` a different one as time passes, but the cached value is served
whatever it is now. Put the value in the query key in the same way; for time, put a rounded
date in the key, such as today's, and filter by that.
