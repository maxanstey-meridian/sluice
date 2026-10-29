# Sources and Queries

Sluice has two kinds of declaration. A `Source<TKey>` is something a cached value can depend
on. A `Query<TKey, TValue>` is something cached, or a `Query<TValue>` when it has no key. Both
are declared once, usually as `static readonly` fields next to each other, and shared by every
reader and writer.

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

`For` and `All` return a `Dependency`, which you can't build any other way. So there are no
hand-built strings for a reader and a writer to spell differently.

### Names

A source's name can't be blank. Declaring the same name again with another key type, or
another `format`, throws `ArgumentException`: the two would give one key different text, so a
write through one would miss the other's readers, which would keep serving the old value. Two
declarations with the same key type and format are allowed, and share evictions. Declare each
source once and use that one declaration everywhere. As for queries, see below for how formats
are compared.

With the EF Core package, the names it gives a model's dependencies are its own: `Customers`,
`Orders[CustomerId]`, `Pages(Slug)` and the rest are named after tables (see
[EF Core](/guides/ef-core)). A source of yours with one of those names would have its own key
text, so a save through EF would miss its readers, and your writes would miss EF's. So it
is refused, whichever comes first. Declaring your source throws `ArgumentException` once the
model has claimed the name: in a read, `set.Dependency`, or a save or bulk write, which claims
every name of the model, whatever table it writes. Declared first, your source makes the model's
first use throw instead: a read or a bulk write the `ArgumentException`, a save a
`DbUpdateException` wrapping it. To invalidate a table's
dependencies after a write Sluice can't see, use `set.Dependency(key)` or
`set.AllDependencies()` on its `DbSet`.

## Queries

A query has a name, a key type and a value type (or just a name and a value type, for a
[query with no key](#queries-with-no-key)):

- The compiler checks that each compute returns the query's value type.
- Every caller gets the same cache key for the same key value, and the same duration.
- The cache key is `<name>:<key text>`, or just `<name>` for a query with no key. To change
  what a query caches and drop its old entries, rename it.

Query names can't be blank, contain `:` or start with `__fc` (FusionCache keeps its own data
there, and Sluice refuses a FusionCache tag prefix outside it: see
[What Sluice sets](/guides/configuration#what-sluice-sets)). Declaring the same name twice with
different key or value types, or once with a key and once without, throws `ArgumentException`,
because the two would share cache entries: with Redis, one could read the other's value as the
wrong type, with no error. So does declaring it again with another `format`, since one key would
get two texts, splitting its entries or sharing one between two keys. Declaring the same name
with the same types and format again is allowed.

Formats are compared by the method they compile to. One lambda is one format, whatever it
captures, so declaring through a shared helper works; two lambdas written separately are two
formats even when their text is the same, so declare the query once and use that declaration
everywhere. A format compiled from an expression at run time isn't compared. Sluice can't catch
a clash between processes, nor one lambda that formats differently depending on what it
captured.

### Queries with no key

A value with only one entry, such as a list of every plan, is a `Query<TValue>`. Its cache key
is its name, and its call takes no key:

```csharp
// Each plan, keyed by its code.
public static readonly Source<string> Plans = new("plan");

// The list of every plan: one entry, shared by every caller.
public static readonly Query<IReadOnlyList<Plan>> AllPlans = new("all-plans");

public ValueTask<IReadOnlyList<Plan>> GetPlans(CancellationToken ct) =>
    cache.GetOrSetAsync(
        AllPlans,
        async Task<IReadOnlyList<Plan>> (read, ct) =>
            await read.From(Plans.All, ct => catalog.ListPlansAsync(ct)),
        ct
    );
```

Use one only when the value really is the same for every caller. A value that seems global is
often per tenant or per locale: whatever changes what the compute reads belongs in a key, so
declare a `Query<TKey, TValue>` keyed by it instead. Otherwise one tenant's value is served to
every other tenant.

### Duration

`Duration` is how long an entry lives unless a write evicts it first. Leave it unset to use
the cache's default, which is FusionCache's 30 seconds unless you
[configure another](/guides/configuration#durations). If an invalidation is ever missed, the
duration is how long the old value can live (or the query's [`MaxStaleness`](#maxstaleness), if
that's longer), so keep it as short as the data allows.

A cache-wide `MemoryCacheDuration` or `DistributedCacheDuration`, if you set one, still wins for
memory or Redis respectively, as it does in FusionCache. Each time a query is used, Sluice checks
its duration isn't so long that an evicted entry could come back, and throws
`InvalidOperationException` if it is. See
[the lifetime rule](/guides/configuration#the-lifetime-rule).

### MaxStaleness

By default, a write evicts every value that read it, at once. A broad, expensive read over busy
data, such as a dashboard, a count, or a public listing, is then evicted by almost every write,
so it almost never hits. `MaxStaleness` lets such a query lag by a stated time instead:

```csharp
// The open jobs on the public board: read on every page, written all day, fine an hour old.
public static readonly Query<string, JobBoard> JobBoard = new("job-board")
{
    MaxStaleness = TimeSpan.FromHours(1),
};
```

With `MaxStaleness` set to N, every server sees a committed change within N of when Sluice
began computing the value that missed it, give or take how far apart the servers' clocks are. A
value that's read and then invalidated is served for the rest of that time, and computed again
after it. So:

- **Quiet data** keeps its entry for its full `Duration`, as it would without `MaxStaleness`.
- **Busy data** is computed about once per N, however often it's written.
- **A writer can read its own write's old value.** Within N, a request that saves a change and
  then reads the query can get the value from before its save, even though the save has
  committed. That's what opting in means. Where the writer must see its change, read it some
  other way, such as a query without `MaxStaleness`.
- **Data that must go at once**, such as an erasure, goes with `InvalidateNowAsync` instead of
  `InvalidateAsync`: it evicts what this query would still serve. See
  [Invalidating](/guides/invalidation#invalidating), and for EF,
  [Saves that must go at once](/guides/ef-core#saves-that-must-go-at-once).

It works by storing each computed value twice: the usual entry, which a write evicts at once, as
always, and a *stale copy*, which only `InvalidateNowAsync` and `ClearAsync` evict, and which
expires N after its compute began. A read takes the entry, else the copy, else computes. A
compute that takes longer than N leaves no copy, so a write evicts its value at once. Nor does
a compute that stores nothing: one that called [`DoNotCache`](#leaving-a-value-uncached), broke
a rule, or ran while its flow had
[uncommitted writes](/guides/strict-reads#reading-your-own-uncommitted-writes).

N needn't be shorter than `Duration`. Longer, a value can outlive its `Duration`, written to or
not: once the entry expires, the copy is served until N. A `MaxStaleness` of zero or less throws
`ArgumentOutOfRangeException`, `Timeout.InfiniteTimeSpan` included. Like a `Duration`, it's
checked against [the lifetime rule](/guides/configuration#the-lifetime-rule) each time the query
is used.

The cost, paid only by queries that set it:

- each compute writes the value twice, to memory and to the distributed cache, so it's
  serialised twice and counts twice under a [size limit](#size);
- the copies carry tags of their own, so there are twice as many tag markers, and a server's
  first read of a copy looks up each of its tags in the distributed cache, one round trip each;
- a cold miss can make up to about six distributed cache reads, against one or two;
- a read served from the copy costs a miss on the entry first;
- the first read on each server after a write removes the evicted entry from the distributed
  cache and tells the other servers. In a race, that can evict a fresh entry another server has
  just stored, which costs one more compute.

A hit on the entry costs what it does without `MaxStaleness`.

The copy's expiry is a time of day, set by the server that computed it and compared with each
reading server's own clock. So clocks that disagree move the bound by the difference. See
[Limitations](/misc/limitations#multi-node).

### Size

`Size` is the entry's size in Sluice's memory cache, for an app that gives that cache a size
limit (see [Limiting memory](/guides/configuration#limiting-memory)). It's counted in whatever
units the limit uses: one per entry, or an estimate of its bytes. Leave it unset to use the
cache's `DefaultEntryOptions.Size`. Once a limit is set, every entry needs a size, so set the
default or set `Size` on every query. A negative size throws `ArgumentOutOfRangeException`.

```csharp
public static readonly Query<string, PublicForm?> FormBySlug = new("form-by-slug")
{
    Size = 1,
};
```

### Leaving a value uncached

A compute can hand its value to the caller without storing it: call `read.DoNotCache()`. The
next call for the key computes again. Use it when a value isn't worth keeping, such as a miss
on a key from public input: cached, every made-up slug in a URL would take memory.

```csharp
cache.GetOrSetAsync(
    FormBySlug,
    slug,
    async Task<PublicForm?> (read, ct) =>
    {
        var db = read.Db<AppDb>();
        var forms = await read.EntityAsync(db.Forms, f => f.Slug, slug, ct);
        var form = await forms
            .Select(f => new PublicForm(f.Id, f.Title))
            .SingleOrDefaultAsync(ct);
        if (form is null)
        {
            read.DoNotCache();
            return null;
        }

        return form;
    },
    ct
);
```

A compute that calls it needn't record a dependency, since nothing stored needs evicting. It
can be called anywhere in the compute, a fused fetch included, but only on the compute's own
`read`. A violation still wins: a compute that read what Sluice can't track throws whether or
not it called `DoNotCache`. A caller waiting on the same key doesn't share the value: once the
running compute ends with nothing stored, it runs its own, one caller at a time.

### The compute returns exactly `TValue`

A compute for a `Query<CustomerId, IReadOnlyList<Invoice>>` that returns a `List<Invoice>`
doesn't compile, because a `Task<List<T>>` isn't a `Task<IReadOnlyList<T>>`. C# reports CS0411,
"the type arguments cannot be inferred". Give the lambda its return type:

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

Sources and queries turn each key into text. For a query, that text is part of the cache key,
so two different keys must never give the same text, or one key's caller gets the other's
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

### Renaming changes the cache keys

Cache keys, and the tags Sluice evicts by, are built from names and key text. Renaming a source
or query, or changing a `format`, changes them. During a rolling deploy, old and new servers
then stop evicting each other's entries until those entries expire.

## When declarations are checked

The constructors check names and key types, and throw `ArgumentException`. Declarations are
`static readonly` fields, so that happens the first time the class is used, wrapped in a
`TypeInitializationException`. One test per declaring class finds it first (a clash with an EF
dependency's name shows only once the model has been used too, so a test that also saves a change
through each context finds that):

```csharp
[Fact]
public void Billing_declarations_are_valid() =>
    RuntimeHelpers.RunClassConstructor(typeof(Billing).TypeHandle);
```
