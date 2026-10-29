# How It Works

Sluice is glue over FusionCache. It has no cache, lock, queue, or store of its own: dependencies
become FusionCache tags, and invalidation is FusionCache's `RemoveByTag`.

## Dependencies become tags

FusionCache's tagging works like this. Each entry carries a list of tags and the time it was
stored. `RemoveByTag(t)` records when `t` was invalidated (the tag data), in memory and in the
distributed cache, and tells other nodes over the backplane. Nothing is deleted up front: when an
entry is read, FusionCache checks its tags, and an entry stored before one of its tags was last
invalidated is treated as gone.

Sluice maps each dependency to a pair of tags. For a source named `n` and a key whose text is
`k`:

| Dependency | A compute that reads it gets the tags | Its stale copy gets | Invalidating it removes the tags | Invalidating it hard also removes |
|---|---|---|---|---|
| `source.For(key)` | `n:k`, `n:` | `!n:k`, `!n:` | `n:k`, `n:*` | `!n:k`, `!n:*` |
| `source.All` | `n:*`, `n:` | `!n:*`, `!n:` | `n:` | `!n:` |

So invalidating a key evicts readers of that key and readers of the whole source; invalidating
the whole source evicts every reader of anything in it. Every tag contains `:`, and Sluice refuses a
clear tag with `:` after a non-blank start (`a:b`, not `:rem`), so none can collide with FusionCache's own clear
markers. Two sources whose tags happen to coincide only
share evictions.

A [stale copy](#a-query-with-maxstaleness) carries its compute's tags with `!` in front, its
*now-tags*, so a plain invalidation never reaches it. Only a clear, or a *hard* invalidation,
which removes the now-tags too, does: `InvalidateNowAsync`, `InvalidateNow`, and every
invalidation or EF write started inside `SluiceFlow.InvalidateNow()`. A now-tag still has `:`
after a non-blank start, so it can't equal a clear marker either. A source whose name starts with `!` can share tags with another source's now-tags, which,
as above, only shares evictions.

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

### A query with MaxStaleness

A query with `MaxStaleness` set to N first reads its entry, then its stale copy, under the key
`:` plus the entry key, which no entry key or tag-data key can start with. Both reads refuse an
expired entry, whatever fail-safe or `AllowStaleOnReadOnly` you set, since either would be served
past its life. On two misses, it calls `GetOrSetAsync` on the copy's key, and the copy's factory
makes the usual call on the entry's key with the same `read`. So the copy is stamped before the
compute starts, and a hard invalidation that lands while the compute runs removes the copy
too. A plain invalidation that lands while the compute runs leaves the copy, so the next caller
gets it until N. The copy's factory keeps the value only if its own compute stored the entry: it
tags the copy with the entry's tags as now-tags, with fail-safe, jitter, and
`MemoryCacheDuration` off. In memory, FusionCache counts the copy's life from when it's stored,
so the factory gives it N less the time since the factory started. The factory's start and end are
both read from `DateTimeOffset.UtcNow`, the clock FusionCache stamps and expires entries on, not the
`TimeProvider` registered in DI. In the distributed cache, FusionCache counts from the copy's
stamp, so the factory sets `DistributedCacheDuration` to N. Either way the copy ends no later
than N after its compute began. When its own compute stored nothing, or N has already passed,
the factory skips the memory write, the distributed write, and the backplane message, as the
guards below do. N counts from the compute, not the write, because FusionCache keeps only a
tag's latest invalidation, not the first since an entry was stored. A query without
`MaxStaleness` makes the single `GetOrSetAsync` call, and never touches a copy.

### The guards

A compute that recorded nothing, called `GetOrSetAsync` from inside itself, or used another
compute's `read`, is a bug. Sluice doesn't throw from the factory, because FusionCache's
fail-safe would catch the exception and serve a stale value instead. The factory records the violation, tells FusionCache to skip the
memory write, the distributed write, and the backplane message, and returns. `GetOrSetAsync` then
throws once FusionCache returns. The EF package's strict-read checks use the same route, so a
compute that catches their exception still caches nothing. `read.DoNotCache()` takes the same
route, skipping the three writes, but hands the value back instead of throwing. A package can
also register a store check with the app's services, which the cache takes when it's built and
runs on every value about to be stored; the EF package's refuses one holding a lazy loader or a
`DbContext`, and a refusal takes the guards' route too.

A compute also runs outside the caller's ambient transaction (`TransactionScope`), so a
connection it opens can't see the caller's uncommitted rows. And it can hold resources for its
own lifetime: the EF package's DI scope for `read.Db` is one, opened on first use, with the
contexts it built. Everything a
compute holds is disposed when it ends, newest first. A dispose that throws takes the guards'
route too, so it can't hide the compute's own exception or let fail-safe serve a stale value.

"A compute is running" is an `AsyncLocal` set inside the factory, so it flows into tasks the
compute starts, and not into anything else.

### Reads inside your own transaction

`SluiceFlow.Begin()` installs a box on the flow (another `AsyncLocal`); a nested call reuses the
outer one. The EF package adds a probe to it for each EF transaction it sees begin, be used, or
be written in, keyed by EF's transaction id, and the box keeps one per cache and key. A probe
returns that transaction's pending dependencies, less what a rollback to a savepoint undid. It
holds the context's connection weakly and reports only while the connection is still in the
transaction it was added for, matched by EF's transaction id, since a provider may reuse the
`DbTransaction` object for a later transaction. When the transaction was committed or rolled
back outside EF (its `DbTransaction` has no connection, or won't report its isolation level),
the probe publishes what it held and reports nothing. For an ambient transaction the package
keeps its own list, read with the caller's `Transaction.Current` before the compute suppresses
it; for an enlisted transaction that isn't the current one (`EnlistTransaction` without a
scope), each write in it adds a probe for that list, keyed by the transaction, to the writing
flow's box. `GetOrSetAsync` asks the probes; with no box that's one `AsyncLocal` lookup, plus a read of
`Transaction.Current` once the EF package has registered its ambient lookup. If anything is pending, the call skips FusionCache
entirely, so it never joins another caller's stampede lock: it runs the compute through the same
`read`, guards, and held resources, stores nothing, and intersects the tags the compute read with
the tags the pending writes will invalidate. Any overlap throws; none returns the fresh value.

## An invalidation

`InvalidateAsync` turns the dependencies into tags, de-duplicates them, and removes each by tag,
concurrently. With a distributed cache, each removal is a write to it and a backplane message.
It takes no cancellation token and passes none, so an aborted request still invalidates.
`Invalidate` is the same, synchronously, one tag at a time. `InvalidateNowAsync` and
`InvalidateNow` remove each tag's now-tag too, and so do `InvalidateAsync` and `Invalidate`
called inside `SluiceFlow.InvalidateNow()`. The scope is an `AsyncLocal`, set when it opens and
put back to what the flow had when it's disposed, so scopes nest, and a task started inside one
keeps it. The call reads it on the caller's flow before it awaits anything.

Then it schedules the same removal again, on the `TimeProvider` registered in DI (`ClearAsync`
does the same with its clear): in the window due at the first whole second at least 5 s ahead,
timed on the provider's steady clock, so 5 to 6 s later. A window has one timer and a
de-duplicated set of tags, published together when it fires. FusionCache 2.9 writes a tag's marker without the
lock its tag check holds, so an invalidation landing in the middle of a check on the same server
can be overwritten by what the check read (FusionCache#659). The check that won leaves its
marker in memory, so the second removal sticks, unless a check's distributed cache read on that
server takes longer than 5 s. Disposing `SluiceCache` runs every scheduled removal at once and
waits for them.

## EF Core reads

The EF package builds core sources from the model, once per model:

- one per root entity type, named after its table (`schema.table` when the model gives a
  schema), so reads and writes of derived types meet, and so do two classes over one table,
  keyed by the primary key;
- one per foreign key, named `<table>[<FK columns>]`, keyed by the foreign key value;
- one per unique column, named `<table>(<column>)`, keyed by the column's value.

Keys go through the property's value converter to the value the database stores, then are folded
(decimal scale, string case and trailing spaces, dates to the microsecond), so values the
database treats as equal give one tag. Before building any of them, the package checks that
every converter on a key, foreign key, or unique column is one it can trust to round-trip: marked
`[SluiceRoundTrips]`, or one of EF's lossless built-ins.

`read.Db<TContext>()` takes the compute's own context from a DI scope the compute opens for
itself, with no-tracking queries, runs its configure if it has one, and disposes the scope, and
the context with it, when the compute ends. It makes the context on a clean flow, an empty
`ExecutionContext`, so neither the context nor its constructor's services see the caller's
async state, and the configure's services resolve the same way. A pooled registration's context
is built from its options rather than leased. Once per context type, it checks that another
scope of the app gets a different instance, and refuses a shared one.

Recording what a query reads is one EF interceptor for the whole process:

- **On every query compile** (`QueryCompilationStarting`), inside a compute or not: the query's
  expression tree is walked for the entity types it reads: its roots, joins, subqueries, every
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
  context the compute didn't configure. A read inside a transaction throws. On a configured
  context, a property marked `[SluiceContextState]` that a filter reads is read again on the
  clean flow, and two different values throw. Outside a compute it returns at once.

A context without `UseSluice` has no Sluice interceptor, so the package also listens to EF's
diagnostic events, which EF raises for every context, from `AddSluiceEntityFrameworkCore` on:
inside a compute, a command on such a context throws when it's initialised: a write anywhere,
a read outside a fused fetch, and a read in one inside a transaction. A command with no context
at all (a provider's own, such as a sequence generator's) throws anywhere inside a compute.

`read.Entity`, `read.EntityAsync`, and `read.Children` record a narrower dependency, one row (by key, or by a unique
column's value) or one parent's rows, then return the set as a no-tracking query with
auto-includes off, filtered to the key or value (`Entity`) or foreign key value (`Children`). The query is marked, so its tag leaves its own type out unless it
reaches that type again.

A read by a unique column (`read.EntityAsync`) first runs a marked query on the same context,
ignoring query filters, for the keys of the rows holding the value, and records each key's
dependency, or the whole type past 100 rows, before it returns the query. First, because a row
deleted by key between the compute's query and a later lookup reports only its key, which that
lookup would no longer find; any row the query finds after the lookup either was looked up or got
the value afterwards, which reports the value. A lookup that throws makes `EntityAsync` throw.

## EF Core writes

A second interceptor, one per `SluiceCache`, handles writes. For a save:

1. **The save's first command** (`CommandInitialized`, from `SaveChanges`): reads the changed
   entries once, with change detection off, after every `SavingChanges` interceptor and EF's own
   detection and cascades, and snapshots the original values of their foreign keys and unique
   columns. EF overwrites those when it accepts the save. Later batches and retries find the
   save already captured. An original is what EF loaded, so it's only used
   where EF's `UPDATE` or `DELETE` checks the database still holds it: a key, a concurrency
   token, or any column of a row with a row version. Otherwise the foreign key, or the unique
   column, is invalidated whole.
2. **`SavedChanges`**: builds the dependencies, now that generated keys are real. Each row gives
   its key, and its foreign keys and unique columns before and after (or whole); a deleted row also gives every
   foreign key that points at it, and the whole of every type the model lets the database cascade
   into or set to null (worked out once per type); an owned row gives its owner; a row sharing
   its table with another type (table splitting) gives that type's row too.
3. **Publish**, depending on the transaction EF used, read the way EF's batch executor reads it:
   - none: invalidate now;
   - an EF transaction: hold the dependencies against the transaction, and invalidate on
     commit, rollback, or failure, or once Sluice sees it was ended outside EF;
   - an enlisted or ambient transaction: invalidate from its `TransactionCompleted` event;
   - none where it completes, but a live `TransactionScope` its connection was opened in, or its
     first command ran in: invalidate now, and again from that scope's `TransactionCompleted`.

An `ExecuteUpdate` or `ExecuteDelete` publishes the same way, once it has run: EF drops query tags
from these statements, so the tables it wrote are found by their names in its SQL, quoted
or not, wherever one stands alone, and
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

A failed, cancelled, or conflicting save publishes the same way: never drop, because
over-invalidating is safe. After a commit, a failure to invalidate is logged and never thrown.
Sync EF calls publish synchronously, as `Invalidate` does, never by blocking on async code.

A save or bulk write started inside `SluiceFlow.InvalidateNow()` publishes hard. It reads the
scope once, on its own flow, when it starts: a save when `SavingChanges` runs, a bulk command when
EF initialises it. Every dependency it derives keeps that answer, so it publishes the same way
wherever and whenever it publishes: at once, at a commit or rollback after the scope has ended,
from a `TransactionCompleted` event, or when its context's next save finds it unfinished. One
started outside the scope publishes plain, even when its transaction commits inside one. The
scope can't be read at publish time, because that often runs on another flow, or after the
scope has ended.

Every invalidation, EF or not, removes tags the same way, through the same paths as the public
`InvalidateAsync`, `Invalidate`, `InvalidateNowAsync`, and `InvalidateNow`; EF's own saves and
bulk writes publish as the first two do, or as the last two do when they started inside
`SluiceFlow.InvalidateNow()`.
