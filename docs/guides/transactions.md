# Transactions

Invalidation waits for the commit. If Sluice invalidated a write before its transaction
committed, another request could recompute from the old data in between and cache it again,
and nothing would evict that value until it expired.

## The generic path

You call `InvalidateAsync`, so call it after your commit returns. See
[Reading and Invalidating](/guides/invalidation#after-the-commit-never-before).

## EF Core

Sluice watches EF's own transaction signals and invalidates each save's writes when the
transaction it ran in ends. The call that ends it returns only after the invalidation.

| The save ran | Invalidated |
|---|---|
| with no transaction (EF's implicit one, or autocommit) | before `SaveChanges` returns |
| in `BeginTransaction` | when `Commit` or `Rollback` runs, on whichever context calls it |
| in a `TransactionScope`, or `Database.EnlistTransaction` | when the transaction completes, whatever the outcome |
| in a transaction disposed without commit or rollback | never: nothing committed |
| in a `DbTransaction` you commit outside EF | **never**: Sluice can't see it. Unsupported |

A rollback invalidates too, as does a save that fails, is cancelled or hits a concurrency
conflict. Each might have committed something (an earlier batch, a commit whose result is
unknown), and evicting too much is safe where evicting too little isn't.

Inside an EF transaction, all its saves are invalidated together at the commit. Inside an
ambient transaction, each save is invalidated separately when the transaction completes.

### `TransactionScope` with async code

Create the scope with `TransactionScopeAsyncFlowOption.Enabled`:

```csharp
using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
{
    order.Status = OrderStatus.Shipped;
    await db.SaveChangesAsync(ct);
    scope.Complete();
}
```

Without it, the ambient transaction doesn't flow across `await`. EF and Sluice then see no
transaction when an async save completes, so Sluice invalidates at once, before the scope
completes, which reopens the window above.

A promoted or distributed transaction can complete after `Dispose` returns, and is invalidated
then. A connection string with `Enlist=false` doesn't join the scope, so EF autocommits and
Sluice invalidates in `SaveChanges`, as it should.

### Retrying execution strategies

With `EnableRetryOnFailure`, wrap your own transaction in the execution strategy and accept the
changes only once it has committed. This is EF's own pattern; with it, only the attempt that
commits is invalidated:

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
    await transaction.CommitAsync(ct);
});
db.ChangeTracker.AcceptAllChanges();
```

Accepting changes before the commit would leave a retry with nothing to save. A failed
attempt's writes roll back with it, or are invalidated anyway if its commit failed.

### Contexts sharing a transaction

Several contexts can share one transaction. Every context that saves must use `UseSluice` with
the same `SluiceCache`, so its saves are captured. Then:

- **`UseTransaction`**: the context that calls `Commit` or `Rollback` must use `UseSluice` with
  that cache too. It publishes what all of them saved.
- **One `TransactionScope`**: no context commits. Each saving context publishes its own saves
  when the scope completes.

## Computes and transactions

A compute must read committed data from the primary. Inside a transaction it would cache rows
other callers can't see yet, and that may roll back, or an old snapshot under `RepeatableRead`
or `Serializable`, then serve them to everyone. From a lagging replica, an invalidation could
arrive before the replica catches up, and the old value would be cached as new.

Inside a compute, an EF read helper's query that runs inside a transaction (the context's EF
transaction, an enlisted one or an ambient one) throws `InvalidOperationException`, and so does
a plain query in a fused fetch. Compute the value outside the transaction, or after it commits.
Outside a compute the helpers run normally, since nothing is cached.

The generic path can't check this: don't call `GetOrSetAsync` inside your own open
transaction. Nothing checks for replicas: point computes at the primary.

## What's not covered

- A crash between the commit and the invalidation loses the invalidation. The entry's duration
  bounds how long the old value lives. There's no outbox.
- A `DbTransaction` committed outside EF isn't seen, and neither is a save or a `UseTransaction`
  commit on a context without `UseSluice`.
