# How It Works

Sluice is glue over FusionCache. It has no cache, lock, queue or store of its own: dependencies
become FusionCache tags, and invalidation is FusionCache's `RemoveByTag`.

## Dependencies become tags

FusionCache's tagging works like this. Each entry carries a list of tags and the time it was
stored. `RemoveByTag(t)` records when `t` was invalidated (the tag data), in memory and in the
distributed cache, and tells other nodes over the backplane. Nothing is deleted up front: when an
entry is read, FusionCache checks its tags, and an entry stored before one of its tags was last
invalidated is treated as gone.

Sluice maps each dependency to a pair of tags. For a source named `n` and a key whose text is
`k`:

| Dependency | A compute that reads it gets the tags | Invalidating it removes the tags |
|---|---|---|
| `source.For(key)` | `n:k`, `n:` | `n:k`, `n:*` |
| `source.All` | `n:*`, `n:` | `n:` |

So invalidating a key evicts readers of that key and readers of the whole source; invalidating
the whole source evicts every reader of anything in it. Every tag contains `:`, so none can
collide with FusionCache's own clear markers. Two sources whose tags happen to coincide only
share evictions.

## A cached read

1. `GetOrSetAsync` builds the entry key, `<query name>:<key text>`, under the `sluice` cache's
   key prefix, and calls FusionCache's `GetOrSetAsync` with Sluice's per-call options (see
   [Configuration](/guides/configuration#what-sluice-sets)).
2. On a hit, FusionCache checks the entry's tags and returns the value.
3. On a miss, FusionCache runs Sluice's factory, which runs your compute with a fresh `read`.
   Each `read.From` adds its tags to `read`, under a lock, so concurrent tasks can share it.
4. When the compute returns, `read` closes (a late `read.From` throws), and its de-duplicated
   tags become the entry's tags.

FusionCache stamps the entry with the time the compute *started*. An invalidation that lands
while the compute runs is later than that stamp, so the entry is already dead when it's stored.
The caller still gets its value; the next caller recomputes.

### The guards

A compute that recorded nothing, called `GetOrSetAsync` from inside itself, or used another
compute's `read`, is a bug. Sluice doesn't throw from the factory, because FusionCache's
fail-safe would catch the exception and serve a stale value instead. The factory records the violation, tells FusionCache to skip the
memory write, the distributed write and the backplane message, and returns. `GetOrSetAsync` then
throws once FusionCache returns. The EF package's strict-read checks use the same route, so a
compute that catches their exception still caches nothing. `read.DoNotCache()` takes the same
route, skipping the three writes, but hands the value back instead of throwing.

A compute also runs outside the caller's ambient transaction (`TransactionScope`), so a
connection it opens can't see the caller's uncommitted rows. And it can hold resources for its
own lifetime: the EF package's DI scope for `read.Db` is one, opened on first use. Everything a
compute holds is disposed when it ends, newest first. A dispose that throws takes the guards'
route too, so it can't hide the compute's own exception or let fail-safe serve a stale value.

"A compute is running" is an `AsyncLocal` set inside the factory, so it flows into tasks the
compute starts, and not into anything else.

### Reads inside your own transaction

`SluiceFlow.Begin()` installs a box on the flow (another `AsyncLocal`); a nested call reuses the
outer one. The EF package adds a probe to it for each EF transaction it sees begin or be used,
returning that transaction's pending dependencies. The probe holds the context's connection
weakly and reports only while the connection is still in the transaction it was added for,
matched by EF's transaction id, since a provider may reuse the `DbTransaction` object for a later
transaction. For an ambient transaction it keeps its own list, read with the caller's
`Transaction.Current` before the compute suppresses it; for an enlisted transaction that isn't
the current one (`EnlistTransaction` without a scope), it adds a probe for that list the first
time a flow with a box writes in it. `GetOrSetAsync` asks the probes; with no box that's one `AsyncLocal` lookup, plus a read of
`Transaction.Current` once the EF package has registered its ambient lookup. If anything is pending, the call skips FusionCache
entirely, so it never joins another caller's stampede lock: it runs the compute through the same
`read`, guards and held resources, stores nothing, and intersects the tags the compute read with
the tags the pending writes will invalidate. Any overlap throws; none returns the fresh value.

## An invalidation

`InvalidateAsync` turns the dependencies into tags, de-duplicates them, and removes each by tag,
concurrently. With a distributed cache, each removal is a write to it and a backplane message.
It takes no cancellation token and passes none, so an aborted request still invalidates.
`Invalidate` is the same, synchronously, one tag at a time.

## EF Core reads

The EF package builds core sources from the model, once per model:

- one per root entity type, named by EF's root type name, so reads and writes of derived types
  meet, keyed by the primary key;
- one per foreign key, named `<root type>[<FK properties>]`, keyed by the foreign key value.

Keys go through the property's value converter to the value the database stores, then are folded
(decimal scale, string case and trailing spaces, dates to the microsecond), so values the
database treats as equal give one tag.

`read.Db<TContext>()` takes the compute's own context from a DI scope the compute opens for
itself, with no-tracking queries, runs its configure if it has one, and disposes the scope, and
the context with it, when the compute ends.

Recording what a query reads is one EF interceptor for the whole process:

- **On every query compile** (`QueryCompilationStarting`), inside a compute or not: the query's
  expression tree is walked for the entity types it reads: its roots, joins and subqueries, every
  navigation (matched by name on every type the instance could be), many-to-many join types, and,
  for each type reached, its query filters and auto-included navigations. Owned types count as
  their owner. The result goes into a `TagWith` on the query, so its SQL starts
  `-- Sluice reads: <types>` every time it runs. EF caches the compiled query, tag included, so
  this runs once per query shape. A query whose reads can't be known (raw SQL, a function, a view)
  is tagged `-- Sluice can't track: <why>` instead. A query whose filters read a field or
  auto-property of the context, or a property marked `[SluiceContextState]`, gets one more line, `-- Sluice reads the context's state: <what>`.
- **On every command** (`CommandInitialized`, before it runs): inside a compute, a write throws; a
  command from a context other than `read.Db`'s throws, unless it runs in a fused fetch; otherwise
  the tag is read, and each type it names is recorded as a whole-type dependency, or the command
  throws if the tag says it can't be tracked, or has none, or reads the context's state on a
  context the compute didn't configure. A read inside a transaction throws.
  Outside a compute it returns at once.

`read.Entity` and `read.Children` record a narrower dependency, one row (by key, or by a unique
column's value) or one parent's rows, then return the set as a no-tracking query with
auto-includes off, filtered to the key or value (`Entity`) or foreign key value (`Children`). The query is marked, so its tag leaves its own type out unless it
reaches that type again.

A read by a unique column also registers a step to run once the compute returns, before its
value is stored, on its flow while `read` still records: a marked query on the compute's
context, ignoring query filters, for the keys of the rows holding the value. It records each
key's dependency, or the whole type past 100 rows. A step that throws fails the compute, and
steps are skipped after a violation. They're skipped after `read.DoNotCache()` too, unless the
caller's transaction has uncommitted writes: then what they record is checked against those
writes, as the compute's own reads are.

## EF Core writes

A second interceptor, one per `SluiceCache`, handles writes. For a save:

1. **`SavingChanges`**: reads the changed entries through `ChangeTracker.Entries()`, which detects
   changes the way EF's own save does, and snapshots the original values of their foreign keys
   and unique columns. EF overwrites those when it accepts the save. An original is what EF loaded, so it's only used
   where EF's `UPDATE` or `DELETE` checks the database still holds it: a key, a concurrency
   token, or any column of a row with a row version. Otherwise the foreign key, or the unique
   column, is invalidated whole.
2. **`SavedChanges`**: builds the dependencies, now that generated keys are real. Each row gives
   its key, and its foreign keys and unique columns before and after (or whole); a deleted row also gives every
   foreign key that points at it, and the whole of every type the model lets the database cascade
   into or set to null (worked out once per type); an owned row gives its owner.
3. **Publish**, depending on the transaction EF used, read the way EF's batch executor reads it:
   - none: invalidate now;
   - an EF transaction: hold the dependencies against the `DbTransaction`, and invalidate on
     commit, rollback or failure;
   - an enlisted or ambient transaction: invalidate from its `TransactionCompleted` event.

An `ExecuteUpdate` or `ExecuteDelete` publishes the same way, once it has run: EF drops query tags
from these statements, so the tables it wrote are found by their quoted names in its SQL, and
every read of the entity types stored in them is invalidated, reads by unique column included
(for a delete, what it cascades into too). One that reports it changed no rows publishes nothing.

A keyed one (`ForKey`, `ForKeys`) adds the key `Where` to the query itself, then sets an
`AsyncLocal` pin, the entity type and its keys, around EF's call, and restores it afterwards,
even when the call throws. The first bulk command EF initialises on that flow during the call
takes the pin and holds it until it ends, so no other command sees it, whatever order
interceptors run in, and a command object the provider hands out again starts without it. When it pins one of the types the command names, with no more than 100 keys, that
type publishes per key instead: each key's
dependency (which reaches whole-type readers too), every foreign key whole, and, for an update,
each unique column whose quoted name appears in the SQL; for a delete, each `CASCADE` or
`SET NULL` foreign key pointing at the type under each key, and what it cascades into. An update
whose SQL names the key's column again, besides Sluice's own condition, may set the key,
so it publishes whole. Every other type the SQL names publishes whole.

A failed, cancelled or conflicting save publishes the same way: never drop, because
over-invalidating is safe. After a commit, a failure to invalidate is logged and never thrown.
Sync EF calls publish synchronously, through `Invalidate`, never by blocking on async code.

Every invalidation, EF or not, goes through the same public `InvalidateAsync` or `Invalidate`.
