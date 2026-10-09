# Strict Reads

Sluice only evicts a cached value when something it recorded changes. An EF query that skips
the read helpers records nothing, so no write would ever evict the value built from it. It
would sit there out of date, silently, until it expired.

So on a context with `UseSluice`, EF reads inside a compute are strict: anything that could
read a row without recording it throws `InvalidOperationException` instead.

## What's checked

### Every command comes from a read helper

Inside a compute, an EF command must carry the read helpers' marker (a `TagWith` comment at the
head of their SQL). These throw when they run:

- a plain query: `db.Users.Where(...)`, `db.Users.FindAsync(id)` when the row isn't tracked yet;
- lazy loading and explicit loading (`Entry(order).Collection(o => o.Lines).LoadAsync()`);
- `FromSql`, `SqlQuery` and raw SQL;
- compiled queries.

The message names the command by the first line of its SQL.

### Writes are refused

`SaveChanges`, `ExecuteSql`, `ExecuteUpdate` and `ExecuteDelete` throw anywhere inside a compute.
A compute is a read. A write inside it would invalidate while the compute is still running,
which can throw away the very value it's building. Write before or after `GetOrSetAsync`.

### A helper's query reads only its own rows

A helper's query may be filtered, ordered, paged and projected. One that reads anything beyond
the helper's own rows throws when EF compiles it:

- a join, a subquery or a set operation over another set or a `SqlQuery`;
- a navigation: `Include`, `order.Customer.Name`, `customer.Orders.Count`, many-to-many
  navigations, and the same reached through a cast, an interface or `EF.Property`;
- a database function you mapped (one mapped with `IsBuiltIn = true` is trusted);
- a query filter that does any of those, or reads the context's state, such as a tenant id
  held on the context. `IgnoreQueryFilters()` leaves the filters out;
- `AsTracking()`;
- entities that load lazily or hold their context: lazy-loading proxies, an injected lazy
  loader or `DbContext`, or an owned value that does.

When in doubt, the check refuses. A member whose name is a navigation on any related entity
type is refused, even where the object can't be that type. `order.Customer!.Id` is refused too,
though EF could read it from the foreign key; use `order.CustomerId`.

Owned types and JSON columns are part of their owner's row, so they're fine. Auto-included
navigations aren't loaded. Read related rows with their own helper call:

```csharp
var order = await read.Entity(db.Orders, orderId).SingleAsync(ct);
var lines = await read.Children(db.OrderLines, line => line.OrderId, orderId).ToListAsync(ct);
```

This check runs whenever EF compiles a helper's query, inside a compute or not, because EF
caches the compiled query and won't run the check again.

### Not inside a transaction

Inside a compute, a helper's query that runs inside a transaction throws: the context's EF
transaction, an enlisted one or an ambient `TransactionScope`. Inside a transaction, a compute
could cache rows other callers can't see yet, which may roll back, or an old snapshot, and serve
them to everyone. See [Transactions](/guides/transactions#computes-and-transactions).

### Catching doesn't help

Sluice also remembers the violation. If the compute catches the exception and carries on, the
call still throws it once the compute returns, and nothing is cached.

## The declared route

Some reads have no helper: a view, a keyless type, raw SQL, a compiled query, a query that
needs a join. Run them in the fetch of a fused `read.From(dependency, fetch)`, with a source you
declare:

```csharp
public static class Reports
{
    // The view of one store's sales, keyed by StoreId.
    public static readonly Source<StoreId> StoreSales = new("store-sales", id => id.Value);
}

var sales = await read.From(
    Reports.StoreSales.For(storeId),
    ct => db.StoreSales.AsNoTracking().Where(row => row.StoreId == storeId).ToListAsync(ct)
);
```

You've said what the fetch reads, so Sluice lets plain EF queries run inside it, and inside
tasks it starts. In return, invalidate that source yourself when the underlying data changes,
after the commit:

```csharp
await db.SaveChangesAsync(ct);
await cache.InvalidateAsync(Reports.StoreSales.For(order.StoreId));
```

Inside a fetch, the other rules still apply: writes throw, nesting throws, reads inside a
transaction throw, and a helper's query still may only read its own rows. A plain query in a
fetch can track, so add `AsNoTracking()` yourself. `read.From(dependency)` without a fetch
allows nothing extra.

## What isn't checked

- **Reads that run no SQL.** `Find` of an entity the context already tracks, `Local`, and
  navigations that are already loaded hand back what the context holds, unsaved edits
  included. Don't use them inside a compute.
- **Raw ADO.NET or Dapper on the context's own connection.** They aren't EF commands.
- **A context without `UseSluice`.** The helpers refuse one, but a plain query on it isn't
  checked.
- **Work started under `ExecutionContext.SuppressFlow`.** It can't see the compute, so every
  guard is off there.
- **Copying the marker** into a `TagWith` of your own gets past the check. Nobody does that by
  accident.
- **An entity that loads lazily, wrapped in your own type** (a DTO or record holding it). The
  check only looks at entities a query returns directly.
- **A plain query in a fused fetch that reads rows its dependency doesn't cover.** You declared
  what the fetch reads; Sluice takes your word for it.
- **A query expression interceptor of your own added after `UseSluice`**, or in
  `OnConfiguring`. It rewrites queries after the check.
- **A database function mapped with `IsBuiltIn = true`.** It's trusted not to read other rows.
- **Outside a compute**, only a helper's query is checked, for reading beyond its own rows. Your
  other queries run untouched.

## Tenants and other context state

A query filter that reads a field on the context (`HasQueryFilter(i => i.TenantId == _tenantId)`)
gives each context different rows, but a cached value is served to every context. So a helper's
query refuses such a filter. Put the tenant in the query key and filter by it yourself:

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
            await read.Children(db.Invoices, invoice => invoice.TenantId, tenantId)
                .IgnoreQueryFilters()
                .Select(invoice => new InvoiceSummary(invoice.Id, invoice.Total))
                .ToListAsync(ct),
        ct
    );
```

Here `TenantId` is a foreign key to a tenants table, so `Children` records one dependency per
tenant. Without such a key, `read.All(db.Invoices).IgnoreQueryFilters().Where(...)` works too,
but more broadly: a write to any invoice, in any tenant, evicts it.

The same goes for any context-dependent filter, such as soft delete toggled per context:
whatever changes what a compute reads must be part of the query key.
