# Sources and Queries

Sluice has two kinds of declaration. A `Source<TKey>` is something a cached value can depend
on. A `Query<TKey, TValue>` is something cached. Both are declared once, usually as
`static readonly` fields next to each other, and shared by every reader and writer.

```csharp
public sealed record InvoiceId(string Value);
public sealed record CustomerId(string Value);
public sealed record Statement(CustomerId Customer, IReadOnlyList<Invoice> Invoices);

public static class Billing
{
    // An invoice, keyed by InvoiceId. Its key text is id.Value.
    public static readonly Source<InvoiceId> Invoice = new("invoice", id => id.Value);

    // The invoices belonging to one customer, keyed by CustomerId.
    public static readonly Source<CustomerId> CustomerInvoices = new(
        "customer-invoices",
        id => id.Value
    );

    // A customer's statement, kept for five minutes unless invalidated.
    public static readonly Query<CustomerId, Statement> Statement = new(
        "statement",
        id => id.Value
    )
    {
        Duration = TimeSpan.FromMinutes(5),
    };
}
```

EF Core entities need no declarations: the EF package builds their sources from the model.
See [EF Core](/guides/ef-core).

## Sources

A source has two kinds of dependency:

- `source.For(key)` is one member, such as one invoice.
- `source.All` is the whole source, every invoice.

A compute records them with `read.From`, and a write invalidates them with
`cache.InvalidateAsync`. They overlap like this:

| The compute read | Evicted by invalidating |
|---|---|
| `Invoice.For(a)` | `Invoice.For(a)` or `Invoice.All` |
| `Invoice.All` | any `Invoice.For(...)`, or `Invoice.All` |

Invalidating `Invoice.For(a)` leaves readers of `Invoice.For(b)` cached.

`Dependency`, the value `For` and `All` return, has no public constructor or members. It can
only come from a source, so there are no hand-built strings for readers and writers to spell
differently.

### Names

A source's name can't be blank. It isn't checked for duplicates: two sources with the same name
share evictions, which over-invalidates but never serves a wrong value. Declare each source
once and use that one declaration everywhere.

## Queries

A query binds a name, a key type and a value type:

- The compiler checks that each compute returns the query's value type.
- Every call site builds the entry key the same way, and with the same duration.
- The cache entry key is `<name>:<key text>`. To version a query, rename it.

Query names can't be blank, contain `:` or start with `__fc` (FusionCache keeps its own data
there). Declaring the same name with a different key or value type throws
`ArgumentException`, because the two would share entries: on a multi-node setup one would read
the other's value from the distributed cache as the wrong type, with no error. Declaring the
same name with the same types again is allowed. Copies in other processes, and same-typed
copies with a different `format`, can't be caught.

There's no keyless query. A value that seems global is usually per tenant or per locale, so key
it by that.

### Duration

`Duration` is how long an entry lives unless something evicts it first. Leave it unset to use
the cache's default, which is FusionCache's 30 seconds unless you
[configure another](/guides/configuration#durations). The duration is the backstop for any
invalidation that's missed, so keep it as short as the data allows.

A cache-wide `MemoryCacheDuration` or `DistributedCacheDuration`, if you set one, still wins at
its level, as it does in FusionCache. A query's duration is checked against the lifetime of
Sluice's tag data each time the query is used, and throws `InvalidOperationException` if it's
too long. See [the lifetime rule](/guides/configuration#the-lifetime-rule).

### The compute returns exactly `TValue`

`Task<T>` is invariant, so a compute for a `Query<CustomerId, IReadOnlyList<Invoice>>` that
returns a `List<Invoice>` doesn't compile. C# reports CS0411, "the type arguments cannot be
inferred". Give the lambda its return type:

```csharp
public static readonly Query<CustomerId, IReadOnlyList<Invoice>> Invoices = new(
    "invoices",
    id => id.Value
);

public ValueTask<IReadOnlyList<Invoice>> GetInvoices(CustomerId customerId, CancellationToken ct) =>
    cache.GetOrSetAsync(
        Invoices,
        customerId,
        async Task<IReadOnlyList<Invoice>> (read, ct) =>
            await read.From(
                Billing.CustomerInvoices.For(customerId),
                ct => invoices.ListAsync(customerId, ct)
            ),
        ct
    );
```

## Key text

Sources and queries turn each key into text. For a query that text is part of the entry key, so
two different keys must never give the same text, or one key's caller is served the other's
value.

Without a `format`, the key type decides:

| Key type | Text |
|---|---|
| `string` | as is |
| `IFormattable`: numbers, `Guid`, enums, `DateOnly`, `TimeSpan`, `char`, your own `IFormattable` ids | `ToString(null, CultureInfo.InvariantCulture)` |
| `DateTime`, `DateTimeOffset`, `TimeOnly` | rejected: the invariant text keeps whole seconds (`TimeOnly`: minutes), so different keys would share an entry |
| interfaces and abstract types | rejected: the runtime type would pick the text, so `int 1` and `long 1` would collide |
| anything else: records, classes, tuples, `bool`, `byte[]` | rejected |

A rejected type takes a `format`, passed once on the declaration:

```csharp
public static readonly Source<InvoiceId> Invoice = new("invoice", id => id.Value);

// The exchange rates that took effect at one instant.
public static readonly Source<DateTimeOffset> RateChange = new(
    "rate-change",
    at => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
);
```

A `format` must give different text for different keys, and so must your own `IFormattable`
types. That's your contract; Sluice can't check it. A `format` that returns `null` throws.

Equal values that print differently, like `1.5m` and `1.50m`, only split the cache: each gets
its own entry, and neither is served for the other.

### Multi-part keys and tenants

Join the parts with a separator they can't contain:

```csharp
public sealed record TenantId(string Value);

public static class Invoices
{
    // One invoice in one tenant.
    public static readonly Source<(TenantId Tenant, InvoiceId Invoice)> Invoice = new(
        "tenant-invoice",
        key => $"{key.Tenant.Value}/{key.Invoice.Value}"
    );

    // Everything in one tenant, invalidated alongside.
    public static readonly Source<TenantId> InTenant = new("tenant-invoices", id => id.Value);
}
```

`Invoice.All` spans every tenant, so "everything in tenant T" needs its own source, as
`InTenant` is here. A write to one invoice invalidates both:

```csharp
await cache.InvalidateAsync([
    Invoices.Invoice.For((tenantId, invoiceId)),
    Invoices.InTenant.For(tenantId),
]);
```

### Key text is wire format

Tags and entry keys are built from names and key text. Renaming a source or query, or changing
a `format`, changes them. During a rolling deploy, old and new nodes then don't evict each
other's entries until those entries expire.

## When declarations are checked

The constructors check names and key types, and throw `ArgumentException`. Declarations are
`static readonly` fields, so that happens the first time the class is touched, wrapped in a
`TypeInitializationException`. One test per declaring class finds it first:

```csharp
[Fact]
public void Billing_declarations_are_valid() =>
    RuntimeHelpers.RunClassConstructor(typeof(Billing).TypeHandle);
```
