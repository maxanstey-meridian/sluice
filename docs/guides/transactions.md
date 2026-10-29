# Transactions

Invalidation waits for the commit. If Sluice invalidated a write before its transaction
committed, another request could recompute from the old data in between and cache it again,
and nothing would evict that value until it expired.

## The generic path

You call `InvalidateAsync`, so call it after your commit returns. See
[Reading and Invalidating](/guides/invalidation#after-the-commit-never-before).

## EF Core

Sluice listens for EF's own transaction events, and invalidates what each save wrote when the
transaction it ran in ends. The call that ends the transaction returns only after the
invalidation has run.

| The save ran | Invalidated |
|---|---|
| with no transaction (EF's implicit one, or autocommit) | before `SaveChanges` returns |
| in `BeginTransaction` | when `Commit` or `Rollback` runs, on whichever context calls it |
| in a `TransactionScope`, or `Database.EnlistTransaction` | when the transaction completes, whatever the outcome |
| in a transaction disposed without commit or rollback | when it's disposed, if EF began it; else when Sluice next looks |
| in a `DbTransaction` you commit or roll back outside EF | late: when Sluice next looks (see below). Commit through EF |

A rollback invalidates too, as does a save that fails, is cancelled or hits a concurrency
conflict. Each might have committed something (an earlier batch, or a commit whose result never
came back), and evicting too much is safe where evicting too little isn't.

Inside an EF transaction, all its saves are invalidated together at the commit. Inside a
`TransactionScope` or an enlisted transaction, each save is invalidated separately when the
transaction completes. A save rolled back to a savepoint is still invalidated when the
transaction ends.

A save made inside [`SluiceFlow.InvalidateNow()`](/guides/ef-core#writes-that-must-go-at-once),
which also evicts the stale copies of queries with `MaxStaleness`, takes the scope when it runs,
not when its transaction ends. It evicts them even when the transaction commits after the scope
has ended, and a save made outside the scope doesn't, even when its transaction commits inside
one.
Bulk updates and deletes work the same way.

### Committing outside EF

EF raises no event when you call `Commit()` or `Rollback()` on the `DbTransaction` itself
(`db.Database.CurrentTransaction.GetDbTransaction()`). So Sluice notices the transaction has
ended the next time it looks: at the flow's next cached read (on a flow that called
`SluiceFlow.Begin()`), the context's next command, a write completing in it, the transaction
being disposed, if EF began it (EF disposes it with the context, too; one you pass in with
`UseTransaction` isn't seen being disposed, so Sluice checks it before every cached read, on
any flow, and every second, until it has ended), or its connection beginning another transaction, opening,
closing (even when you close it yourself) or being disposed, whichever comes first. Until then,
a read can cache the old value again. A transaction disposed without commit or rollback looks the same to
Sluice as one committed outside EF, so it's invalidated too, which only evicts more than it
needs to. A context joining a transaction with `UseTransaction` invalidates what the transaction
already holds, and again when it ends, since you may have committed it and begun another outside
EF, which Npgsql hands the same `DbTransaction` object: for contexts sharing one transaction,
that too only evicts more than it needs to. Commit and roll back through EF (`transaction.Commit()` on the
`IDbContextTransaction`) and none of this applies.

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

Without it, the transaction is lost after the first `await`, and EF fails a save that needs
more than one batch. A save that does run then sees no transaction where it completes. Sluice
noted the scope when the connection was opened, or when the save's first command ran, so it
invalidates straight away and again when the scope completes, and logs a warning (event 5)
once. A read in between can cache the old value, but only until the scope completes.

A distributed transaction can complete after `Dispose` returns, and is invalidated then. A
connection string with `Enlist=false` doesn't join the scope, so each save commits on its own
and Sluice invalidates in `SaveChanges`, as it should.

### Retrying execution strategies

With `EnableRetryOnFailure`, wrap your own transaction in the execution strategy, and only
accept the changes once it has committed. This is EF's own pattern, and with it only the
attempt that commits is invalidated:

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

- **`UseTransaction`**: the context that calls `Commit` or `Rollback` needn't use `UseSluice`;
  the commit or rollback invalidates what every `UseSluice` context saved in it. Each context
  joining also invalidates what earlier ones saved (see
  [Committing outside EF](#committing-outside-ef)).
- **One `TransactionScope`**: no context commits. Each saving context invalidates its own saves
  when the scope completes.

## Computes and transactions

A compute must read committed data from the primary database. Inside a transaction it could
cache rows other callers can't see yet, which may roll back, or an old snapshot under
`RepeatableRead` or `Serializable`, and then serve them to everyone. From a replica that lags
behind, the invalidation could arrive before the replica catches up, and the old value would be
cached as new.

So a compute runs outside your ambient transaction. If you call `GetOrSetAsync` inside a
`TransactionScope`, Sluice suppresses the scope while the compute runs, and you get it back
when the compute ends. A connection the compute opens, `read.Db`'s or your own for Dapper,
doesn't join (enlist in) your transaction, so it reads what's committed.

Inside a compute, two EF cases still throw `InvalidOperationException`:

- **A query on `read.Db`'s context inside a transaction the compute opened**: `BeginTransaction`
  on that context, or a `TransactionScope` the compute opens.
- **A query in a fused fetch on a context already in a transaction**, such as your request's
  context halfway through one.

Compute the value outside the transaction, or after it commits.

What isn't checked: a connection that was already open, or a `DbTransaction`, captured from
outside the compute and used through raw ADO.NET or Dapper. Suppressing the scope doesn't take
an open connection out of its transaction, so the compute would read that transaction's
uncommitted rows. Open a fresh connection inside the compute instead. Nothing checks for
replicas either: point computes at the primary.

Because a compute runs outside your transaction, it can't see what your transaction has saved
but not committed. A cached read that depends on such writes throws rather than return data
older than your own write: see
[Reading your own uncommitted writes](/guides/strict-reads#reading-your-own-uncommitted-writes).
Sluice sees a `TransactionScope` by itself, and an EF transaction once the flow writes in it
after calling `SluiceFlow.Begin()`.

## What's not covered

- A crash between the commit and the invalidation loses the invalidation, and the old value
  lives until its entry expires. That's deliberate: Sluice keeps no outbox, so the entry's
  duration bounds it. An app that needs more can record what it wrote in its own outbox, in the
  same transaction, and call `InvalidateAsync` from the outbox's handler.
- A save on a context without `UseSluice` isn't seen.
