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

A compute that recorded nothing, or called `GetOrSetAsync` from inside itself, is a bug. Sluice
doesn't throw from the factory, because FusionCache's fail-safe would catch the exception and
serve a stale value instead. The factory records the violation, tells FusionCache to skip the
memory write, the distributed write and the backplane message, and returns. `GetOrSetAsync` then
throws once FusionCache returns. The EF package's strict-read checks use the same route, so a
compute that catches their exception still caches nothing.

"A compute is running" is an `AsyncLocal` set inside the factory, so it flows into tasks the
compute starts, and not into anything else.

## An invalidation

`InvalidateAsync` turns the dependencies into tags, de-duplicates them, and removes each by tag,
concurrently. With a distributed cache, each removal is a write to it and a backplane message. It takes no cancellation token and passes none, so an aborted request still
invalidates. `Invalidate` is the same, synchronously, one tag at a time.

## EF Core reads

The EF package builds core sources from the model, once per model:

- one per root entity type, named by EF's root type name, so reads and writes of derived types
  meet, keyed by the primary key;
- one per foreign key, named `<root type>[<FK properties>]`, keyed by the foreign key value.

Keys go through the property's value converter to the value the database stores, then are folded
(decimal scale, string case and trailing spaces, dates to the microsecond), so values the
database treats as equal give one tag.

`read.Entity`, `read.Children` and `read.All` record one of those dependencies, then return the
set as a no-tracking query tagged `Sluice read helper` with auto-includes off, filtered to the
key (`Entity`) or foreign key value (`Children`).

Strict reads are one EF interceptor for the whole process:

- **On every command** (`CommandInitialized`, before it runs): inside a compute, a write throws;
  a command without the marker in its SQL throws, unless it runs in a fused fetch; a read inside
  a transaction throws. Outside a compute it returns at once.
- **On every query compile** (`QueryCompilationStarting`): a marked query's expression tree, and
  the query filters EF will add to it, are walked for anything beyond the helper's own rows.

## EF Core writes

A second interceptor, one per `SluiceCache`, handles saves:

1. **`SavingChanges`**: reads the changed entries through `ChangeTracker.Entries()`, which detects
   changes the way EF's own save does, and snapshots their foreign keys' original values. EF
   overwrites those when it accepts the save.
2. **`SavedChanges`**: builds the dependencies, now that generated keys are real. Each row gives
   its key and its foreign keys before and after; a deleted row also gives every foreign key that
   points at it, and the whole of every type the model lets the database cascade into or set to
   null (worked out once per type); an owned row gives its owner.
3. **Publish**, depending on the transaction EF used, read the way EF's batch executor reads it:
   - none: invalidate now;
   - an EF transaction: hold the dependencies against the `DbTransaction`, and invalidate on
     commit, rollback or failure;
   - an enlisted or ambient transaction: invalidate from its `TransactionCompleted` event.

A failed, cancelled or conflicting save publishes the same way: never drop, because
over-invalidating is safe. After a commit, a failure to invalidate is logged and never thrown.
Sync EF calls publish synchronously, through `Invalidate`, never by blocking on async code.

Every invalidation, EF or not, goes through the same public `InvalidateAsync` or `Invalidate`.
