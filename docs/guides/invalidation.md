# Reading and Invalidating

This is the generic path: any data source, any write. The EF Core package builds on the same
calls, so everything here applies there too.

## Reading

`SluiceCache.GetOrSetAsync` returns a query's cached value, or runs your compute and caches what
it returns:

```csharp
public ValueTask<Statement> GetStatement(CustomerId customerId, CancellationToken ct) =>
    cache.GetOrSetAsync(
        Billing.Statement,
        customerId,
        async (read, ct) =>
        {
            // Records the dependency, then runs the fetch, so the two can't drift apart.
            var rows = await read.From(
                Billing.CustomerInvoices.For(customerId),
                ct =>
                    connection.QueryAsync<Invoice>(
                        new CommandDefinition(
                            "select * from invoices where customer_id = @customerId",
                            new { customerId = customerId.Value },
                            cancellationToken: ct
                        )
                    )
            );
            return new Statement(customerId, rows.ToList());
        },
        ct
    );
```

The compute gets `read`, which records what it reads, and the caller's cancellation token.

### `read.From(dependency, fetch)`

The form to reach for. It records the dependency, then runs `fetch` with the compute's token and
returns its result. What you record and what you fetch sit in one call, so a compute can't
record one thing and read another. The rest of these docs call this a *fused read*, and `fetch`
its *fused fetch*.

- `read.From([a, b], fetch)` records several dependencies for one fetch. The list can't be
  empty: the fetch has to declare what it reads.
- If the fetch throws, its dependency stays recorded. If the exception escapes the compute,
  nothing is cached. If the compute catches it and returns a fallback, the fallback is cached
  *with* that dependency, so invalidating the source still evicts it:

```csharp
string? greeting;
try
{
    greeting = await read.From(
        Dashboards.Greeting.For(userId),
        ct => greetings.GetAsync(userId, ct)
    );
}
catch (HttpRequestException)
{
    greeting = null; // the greeting service is down: show none, until the greeting changes
}
```

### `read.From(dependencies)`

Records without fetching. Use it for a value you already have, a fetch that isn't one call, or
a compute that depends on a whole source:

```csharp
read.From(Billing.Invoice.All);
read.From(invoiceIds.Select(Billing.Invoice.For));
```

Every hit checks each dependency the value recorded, at about 0.2 µs each: a hundred cost about 23 µs a hit. For
thousands, record the source's `All` instead. See [Performance](/misc/performance).

### Rules for a compute

- **It must record something.** A compute that records no dependency makes the call throw
  `InvalidOperationException`, and nothing is cached: nothing could ever invalidate it. For a
  value with no dependencies, use FusionCache directly.
- **No nesting.** Calling `GetOrSetAsync` from inside a compute, or inside a fused fetch, throws.
  If the inner value were already cached, the outer compute couldn't learn what it read. Read
  the underlying data in the outer compute instead.
- **Await every read.** `read.From` is safe to call from several tasks at once inside one
  compute (use `Task.WhenAll` freely), but it throws once the compute has returned.
- **Read committed data from the primary database**: not inside your own open transaction, and
  not from a replica that may lag behind. See
  [Computes and transactions](/guides/transactions#computes-and-transactions).
- **Breaking a rule always throws.** FusionCache's fail-safe, if you turn it on, serves the last
  good value when a compute throws. It doesn't for these rules: the call still throws, and
  nothing is stored.

### Cancellation

The token you pass to `GetOrSetAsync` goes to the compute, and from there to each fused
fetch. A cancelled call returns only once its compute has ended, so the compute is never left
running on a `DbContext` or connection the caller has moved on from. A token that's already
cancelled throws before the cache is read.

When two callers ask for the same uncached key, only one compute runs and the other caller
waits for it. That wait can't be cancelled; it ends when the compute does, or at FusionCache's
`LockTimeout` if you set one.

### Caching a value Sluice didn't compute

There's no `Set`. Every cached value comes from a compute, so Sluice always knows what it read.

## Invalidating

Reads go through Sluice, because it has to see them to record them. Writes don't: you write as
usual, then tell Sluice what changed. Only your code knows when its write has committed, so
Sluice doesn't wrap writes here. EF Core tells Sluice when a save commits, so EF writes need
nothing; see [EF Core](/guides/ef-core#writing).

Once a write has committed, invalidate what it changed:

```csharp
await connection.ExecuteAsync(
    "update invoices set paid = true where id = @invoiceId",
    new { invoiceId = invoiceId.Value }
);
await cache.InvalidateAsync([
    Billing.Invoice.For(invoiceId),
    Billing.CustomerInvoices.For(customerId),
]);
```

| Call | Evicts |
|---|---|
| `InvalidateAsync(source.For(key))` | readers of that key, and readers of `source.All` |
| `InvalidateAsync(source.All)` | every reader of anything in the source |
| `InvalidateAsync([a, b, ...])` | the union, in one call |
| `Invalidate(a, b, ...)` | the same, synchronously |
| `ClearAsync()` | everything Sluice cached |

Values that read none of it stay cached.

### After the commit, never before

Invalidating before the write commits leaves a window: another request can recompute from the
old data and cache it, and nothing evicts it until it expires. Inside a transaction, invalidate
after `Commit`:

```csharp
await using (var transaction = await connection.BeginTransactionAsync(ct))
{
    await connection.ExecuteAsync(
        "update invoices set paid = true where id = @invoiceId",
        new { invoiceId = invoiceId.Value },
        transaction
    );
    await transaction.CommitAsync(ct);
}

await cache.InvalidateAsync(Billing.Invoice.For(invoiceId));
```

EF writes do this for you. See [Transactions](/guides/transactions).

### No cancellation token

`InvalidateAsync` takes no token on purpose. A request aborted just after its write committed
must still invalidate, or the old value stays cached until it expires. With Redis, a failed
Redis write doesn't make it throw, but a slow Redis holds it up until the Redis client times
out; see [When Redis is slow or down](/guides/configuration#when-redis-is-slow-or-down).

### An invalidation during a compute

If something a compute has read is invalidated while the compute is still running, the caller
still gets the value it computed, but it isn't kept: the next call computes again. An
invalidation of something the compute didn't read leaves the new entry alone.

### Mixing with EF Core

EF dependencies and declared ones are the same type, so they batch:

```csharp
await cache.InvalidateAsync([db.Users.Dependency(userId), Billing.Invoice.For(invoiceId)]);
```

### Clearing

`ClearAsync` evicts everything Sluice cached, and fail-safe never serves a cleared value. Other
FusionCaches, even on the same Redis, are untouched.

## Cached values are shared

FusionCache hands every caller the same object from memory. Don't modify a cached value; see
[Shared instances and auto-clone](/guides/configuration#shared-instances-and-auto-clone).
