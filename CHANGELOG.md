# Changelog

## 0.5.0 — breaking

Two gaps found in the Casebridge pilot: a conditional `ExecuteUpdate` of one form evicted every
cached read of forms in every council, and a cached read inside a request's own transaction returned
data older than the request's own write. Then every limit the docs listed that Sluice could
close: what's left in [Limitations](https://maxanstey-meridian.github.io/sluice/misc/limitations)
is what Sluice can't see, or what's inherent to FusionCache, EF or .NET.

- **Breaking: a cached read of your own uncommitted writes throws.** Inside a transaction that
  has saved writes it hasn't committed, a cached read that depends on them throws
  `InvalidOperationException`, in every environment, naming the query and saying to read that
  data inside the transaction, not through the cache, or after it commits. It used to return the
  value from before the write: a hit from the cache, or a miss computed from what's committed.
  While such writes are pending, every cached read on the flow computes fresh, stores nothing and
  skips FusionCache entirely, so a cached value can't slip past the check and other callers never
  wait on it; a read that doesn't depend on the writes gets the fresh value. A `TransactionScope`
  is checked by itself. An EF transaction, and one joined with `Database.EnlistTransaction` (no
  scope), is checked on a flow that called `SluiceFlow.Begin()`, below: from when it begins or
  is joined, or, for one the flow was already in, from its first write in it after the call; a
  transaction written in only before `Begin()` isn't, so call it before opening the transaction.
  Every flow with a box that writes in an enlisted transaction is checked. Writes rolled back to
  a savepoint (a save that failed, EF rolling it back to its own savepoint, or a rollback to one
  of yours) stop counting, and are still invalidated when the transaction ends; savepoints made
  outside EF (ADO.NET's `Save`) aren't seen, so don't mix them with EF's in one transaction.
  Saves without a transaction of yours have committed by the time they return, and never count.
- **New: `SluiceFlow.Begin()`.** `using var _ = SluiceFlow.Begin();` once per entry point (HTTP
  middleware, the message pipeline) lets Sluice see the EF transactions the flow opens, however
  deep. A nested call does nothing. Without it, EF transactions aren't checked, and the first
  write Sluice sees in one logs a `Warning` (event 7), once per cache; with it, a flow with a
  transaction open but nothing pending makes one check per cached read.
- **New: `.ForKey(id)` and `.ForKeys(ids)` for `ExecuteUpdate` and `ExecuteDelete`.**
  `db.Set<FormEntity>().Where(…).ForKey(id).ExecuteUpdateAsync(…)` adds the key condition to the
  statement itself, alongside your `Where`, so a conditional write stays conditional, and evicts
  only that row's readers once it commits, instead of every read of the type. A keyed write
  still evicts every `Children` list of the type's foreign keys; an update also evicts every
  reader by a unique column whose name appears in the statement, and a delete what it cascades
  into. Other types the statement names are evicted whole, as before, and so is the keyed type
  past 100 keys, or when an update's statement names the key column again (it may set the key).
  They return a `KeyedRows<T>`, which only executes, so call them last. A composite key or a
  type not mapped to a table throws `NotSupportedException`, and a key of the wrong type, a null
  key, or a query that isn't an EF set of an entity type, `ArgumentException`, before anything
  runs. Sluice doesn't recognise a key condition in your own `Where`: move `f.Id == id` into
  `ForKey`, rather than keep it in both, which, for an update, names the key column twice and
  evicts whole.
- **Breaking: a read by a unique column is `await read.EntityAsync(set, column, value, ct)`, and
  records the row it found.** It replaces `read.Entity(set, column, value)`. Before it returns the
  query, it runs one more query, an indexed lookup of the keys of the rows holding the value,
  ignoring query filters, and records each by its key (the whole type past 100 rows), so a keyed
  write or `set.Dependency(key)` evicts it. It looks up before your query, so a row deleted by key
  in between is still recorded (a keyed delete reports the row's key, not its value), and inside a
  fused fetch too, on the fetch's context. That's one more round trip per cache miss; hits are
  unchanged. If the lookup fails, `EntityAsync` throws, and unless the compute catches it, nothing
  is cached. Naming the primary key runs
  no lookup. SQL snapshot or query-count tests will see the extra query, before the compute's own.
- **Changed: `set.Dependency(key)` evicts readers by a unique column that found that row,** and
  so does a keyed bulk write. A cached "not found" by a unique column still needs
  `AllDependencies()` after a write Sluice can't see.
- **Breaking: an app using the EF Core package calls `services.AddSluiceEntityFrameworkCore()`;
  `UseSluice` throws without it.** Call it where you call `AddSluice`; a second call does
  nothing. It starts Sluice's watch for contexts without `UseSluice` inside a compute, and
  registers the check on cached values (both below), so both cover the very first compute.
- **Breaking: EF dependencies are named after tables, not classes.** An entity type's
  dependencies are named after its root's table (`Customers`, or `sales.Customers` with a
  schema), a foreign key's after its columns (`Orders[CustomerId]`), and a unique column's after
  its column (`Pages(Slug)`); error messages show these names. So a second class mapped to the
  same table, such as a read model in another context, now evicts and is evicted by the first,
  and moving or renaming a class changes nothing. Two types splitting one table share its
  dependencies, so they evict each other's readers of a row. Classes link only when their key
  columns and schema spelling match. Every EF dependency name changes, so see Upgrading.
- **Breaking: converters on keys, foreign keys and unique columns need `[SluiceRoundTrips]`.**
  A save invalidates by the value it would write back for what EF loaded, so a converter that
  loads one stored value and writes back another (`"01"` loaded as 1 and written as `"1"`) left
  the readers of the stored value stale. Sluice now refuses the model, the first time it uses
  it, with `InvalidOperationException` naming each such property, unless the converter's class
  is marked `[SluiceRoundTrips]` (your promise that it round-trips) or is one of EF's lossless
  built-ins: `EnumToNumberConverter`, a widening `CastingConverter`, `DateTimeToTicksConverter`
  and `GuidToBytesConverter`. Every typed-id converter needs the mark, and one written as
  `HasConversion(id => id.Value, value => new UserId(value))` moves into a class deriving from
  `ValueConverter<TModel, TProvider>` first. Enum-to-string and the other string-parsing
  built-ins need it too, since they accept more than one spelling of a value. A key, foreign key
  or unique column whose date the database rounds (SQL Server's `datetime` or `smalldatetime`,
  MySQL's `datetime` or `timestamp` without a size, a `DateTime` in a `date` column, a date in a
  time-of-day column, a precision under 6) is refused at first use too, and the mark
  doesn't lift that: its new rows' keys missed readers. A date type without a precision is
  trusted only from PostgreSQL and SQL Server's `datetime2` and `datetimeoffset`; on another
  provider, set `HasPrecision(6)` or more.
- **Breaking: a context without `UseSluice`, or an EF command with no context, throws inside a
  compute.** Its commands used to go
  unseen, so a compute that read through one cached a value nothing evicted. Sluice now watches
  EF's diagnostic events for every context, and inside a compute such a command throws before
  it runs: a write anywhere, a read outside a fused fetch, and a read in a fused fetch inside a
  transaction. Add `UseSluice` to its options and query it through `read.Db`, or read it in a
  fused fetch. An EF command with no context (a provider's own, such as Npgsql's HiLo key
  sequence on `Add`) throws anywhere inside a compute, a fused fetch included, where 0.4.0 let a
  read run: add or attach entities before or after `GetOrSetAsync`, not inside it.
- **Breaking: a compute's context and its services are made without the caller's request.**
  They were made on the first caller's async flow, so a tenant provider over
  `IHttpContextAccessor` read that caller's tenant. Now the context, its constructor's scoped
  services and those the configure resolves are made without the caller's async state, so a
  scoped service whose constructor requires the HTTP request fails inside a compute. A property
  marked `[SluiceContextState]` is now checked too: each query reads it on the compute's flow
  and again without the caller's async state, and two different values (or a throw without
  the request) throw `InvalidOperationException`, rather than cache one caller's rows for
  everyone. Set that state in the configure, from the key.
- **Breaking: a query expression interceptor after `UseSluice` is refused.** One added later in
  the options, or in `OnConfiguring`, rewrote queries after Sluice had worked out what they
  read. `read.Db` and the read helpers now throw `InvalidOperationException` naming it; add it
  before `UseSluice`.
- **Breaking: a cached value holding a lazy loader or a context is refused.** A lazily loading
  entity wrapped in your own type (a DTO holding it) slipped past the check on a query's result
  type. Every value is now checked before it's stored, and one holding a lazy loader or a `DbContext` throws, naming the query and where in
  the value it is. Nothing is cached.
- **Breaking: a context registered as a singleton is refused.** `read.Db` checks, once per
  context type, that two DI scopes get different instances, and throws before the compute's
  first query if not, since every compute and request would share it.
- **Breaking: redeclaring a query or source with another `format` throws.** One key got
  different text in each, which split or mixed a query's entries, and made a write through one
  source miss the other's readers. Source names are now checked as query names are: another key
  type or another format throws `ArgumentException`. Formats are compared by the method they
  compile to, so two lambdas written separately differ even with the same text; declare once
  and share the declaration.
- **Breaking: an app `Source` named like an EF dependency is refused.** EF dependencies are now
  named after tables (above), so `new Source<Guid>("Customers")` would share their tags with
  key text of its own, and a write through one would miss the other's readers. Declaring it
  throws `ArgumentException` once a model has used that name; declared first, the model's
  first use throws instead: a save `DbUpdateException` wrapping the `ArgumentException`, a bulk
  write or a read the `ArgumentException` itself. Every name of a model is claimed before its
  first save or bulk write, whichever table it writes. Invalidate EF data through
  `set.Dependency(key)` or `set.AllDependencies()`, and give your own sources other names.
- **Breaking: a FusionCache tag prefix or clear tag that collides with Sluice's is refused.**
  Resolving `SluiceCache` throws `InvalidOperationException` when `InternalStrings`'
  `TagCacheKeyPrefix` doesn't start with `__fc`, which let a query's entry be taken for a tag's
  marker and never invalidated, or when `ClearRemoveTag` or `ClearExpireTag` has a `:` with a
  name before it, which a dependency's tag could equal. `SetToSafeStrings()` with its defaults
  is fine.
- **Breaking: `Sluice.Infrastructure.ActiveCompute` is internal.** It was a hidden public hook
  for the EF package, and the hooks 0.5.0 adds beside it are internal too, so only Sluice's own
  packages can reach them: an app can't build a source with an EF dependency's name, or mark a
  compute itself.
- **Breaking: `SluiceCache` is `IAsyncDisposable` and `IDisposable`.** Every invalidation and
  clear is now published again 5 to 6 s later (below), and disposing the cache, which the service
  provider does at shutdown, publishes whatever is still waiting at once and waits for it. A
  provider disposed synchronously calls `Dispose()`, which blocks until those writes finish.
- **Fixed: a FusionCache 2.9 race could lose an invalidation on one server.** A tag check
  running at the same moment could overwrite it, and that server served the old values until
  they expired (FusionCache#659). Sluice now publishes every invalidation and clear again 5 to
  6 s later, timed by the `TimeProvider` registered in DI, which restores it unless a Redis
  lookup on that server takes longer. Those due in the same second go out together, each tag
  once, on one timer, so an invalidation costs about what it did before. A value cached in
  between is computed once more.
- **Changed: interceptor order no longer matters for saves.** Sluice reads what a save writes
  when EF initialises its first command, after every `SavingChanges` interceptor (soft delete,
  auditing), EF's change detection and its cascades, instead of in its own `SavingChanges`.
  Changes an interceptor after `UseSluice` made used to be missed. The tracker is read once per
  save, and Sluice never detects changes itself. A save that reports rows written but ran no
  command Sluice saw logs an `Error` (event 6) and invalidates every entity type in the model,
  since what it wrote can't be known. `UseSluice` adds its reads interceptor first,
  so a save refused inside a compute invalidates nothing.
- **Fixed: client cascades with mismatched cascade timings.** With `CascadeDeleteTiming` and
  `DeleteOrphansTiming` set differently, a row EF deleted or set to null through
  `ClientCascade` or `ClientSetNull` during the save wasn't captured: its readers by key and by
  unique column, and for a delete what it cascades into, kept the old values. It is now.
- **Fixed: a `TransactionScope` without `TransactionScopeAsyncFlowOption.Enabled`.** An async
  save that couldn't see its scope where it completed invalidated before the commit, and a read
  in between cached the old value for good. Sluice notes the scope when the connection opens or
  the save's first command runs, and invalidates at once and again when the scope completes,
  logging a `Warning` (event 5) once. Bulk writes too. Keep using `Enabled`: EF fails a
  multi-batch save without it.
- **Fixed: a `DbTransaction` committed or rolled back outside EF.** Its writes were never
  invalidated. Sluice now sees that it
  ended the next time it looks (the flow's next cached read, the context's next command, the
  transaction's disposal if EF began it, its connection beginning another transaction, opening,
  closing, even when you close it yourself, or being disposed, and for one you passed in with
  `UseTransaction`, the next cached read on any flow, or within a second, so a server with no
  reads of its own doesn't leave the others serving the old value) and invalidates then, a pooled context's
  or a connection you opened yourself included. A transaction disposed without commit or rollback looks the same, so it now
  invalidates what it wrote, at its disposal if EF began it, which only evicts more than needed.
  A context joining a transaction with `UseTransaction` invalidates what the transaction already
  holds, and again when it ends, since you may have committed it and begun another outside EF,
  which Npgsql hands the same object; for contexts sharing one transaction that also only evicts
  more than needed.
  A write on a context whose connection you closed and opened again yourself, which runs
  outside the transaction the context still holds, is invalidated at once rather than with
  that transaction.
- **Fixed: a commit or rollback on a context without `UseSluice`,** of a transaction a
  `UseSluice` context saved in through `UseTransaction`, invalidated nothing. It now invalidates
  at the commit or rollback.
- **Changed: a compute's context never comes from a pool.** A lease went back with what the
  compute's configure set (a tenant, a connection string) into a request's next lease, and kept
  the services an earlier lease gave it. Sluice now builds a pooled registration's context from
  its options, with its constructor's services from the compute's scope. With the checks below,
  a miss through EF costs about 32 µs more than without Sluice on in-memory SQLite (about 3 µs
  in 0.4.0), and a save about 7 µs; hits are unchanged. See
  [Performance](https://maxanstey-meridian.github.io/sluice/misc/performance).
- **Upgrading:** with a distributed cache, call `ClearAsync` once every node runs 0.5.0. Until
  then neither version evicts the other's entries: every EF dependency is renamed (now after
  tables), and an entry cached before 0.5.0 for a read by a unique column doesn't carry the key
  of the row it found, so a keyed write or `set.Dependency(key)` doesn't evict it. An in-memory
  cache starts empty anyway.
- **Docs:** how to keep a `[SluiceContextState]` setter safe: one helper on `read` as its only
  caller, held by an architecture test. Limitations gains the refusal of unique columns and
  foreign keys whose value converter stores null as a value, and loses every limit this release
  closed. A crash between a commit and its invalidation is now described as the deliberate
  trade-off it is: the entry's duration bounds it, and an app that needs more calls
  `InvalidateAsync` from its own outbox's handler. Transactions gains "Committing outside EF".

## 0.4.0

Three gaps found while planning Casebridge's first cached reads: a tenant filter that reads a
scoped service, a lookup by a unique column, and keys from public input.

- **New: `read.Db<TContext>((db, services) => …)`.** The configure gets the compute's own DI
  scope too (on a context `AddDbContext` or a scoped factory makes, the one it was made in), so
  it can set a scoped service the context reads:
  `read.Db<AppDb>((db, services) => services.GetRequiredService<TenantProvider>().Set(tenantId))`.
  It runs where `read.Db<TContext>(db => …)` runs (once, on the compute's first
  `read.Db<TContext>`, before any query) and fails the same way. The `db => …` overload stays.
- **New: `[SluiceContextState]`, for a query filter that reads a scoped tenant service.** Mark a
  property of the context whose body reads only what the compute's configure sets
  (`[SluiceContextState] private Guid? TenantId => _tenant.Id;`) and a filter may read it inside
  a compute, exactly as it may read a settable field: only on a configured context, with the
  `-- Sluice reads the context's state: …` comment. Sluice doesn't read the body; the attribute is
  your promise, as the configure is. It must hold a plain value, have a body and not be virtual
  or abstract (an interface's implementation is fine); it may be private. The mark does nothing
  on an auto-property: a settable one is already accepted, and a get-only or init-only one is
  still refused. On a context leased straight from `AddDbContextPool`, see the pooling note in the
  guide. Unmarked properties with a body, methods and members of objects the
  context holds are still refused, and the refusal of an unmarked context property now says to
  mark it.
- **New: `read.Entity(set, column, value)`, by a unique column.** A lookup by slug or email
  depends on the row holding that value, not on every row of the type: a save of another row
  doesn't evict it; one that gives a row the value, changes the row holding it, or moves the
  value off it, does, so a cached miss is evicted once a row gets the value. The column must be
  covered by a single-column unique index (a filtered one counts) or alternate key in the EF
  model; anything else throws `ArgumentException`, and a composite one, or one whose value
  converter stores null as a value (`convertsNulls`, internal in EF), `NotSupportedException`.
  `ExecuteUpdate`, `ExecuteDelete`, `AllDependencies()` and database cascades evict these
  readers wherever they evict `read.Entity` by key. As with `Children` lists, a type without a
  row version evicts every reader by its unique columns on each update or delete, and the
  `Information` log (events 3 and 4) now says so. `set.Dependency(key)` doesn't evict them;
  `AllDependencies()` does.
- **New: `read.DoNotCache()`.** The compute's value goes to its caller but isn't stored, and the
  next call computes again: `if (form is null) { read.DoNotCache(); return null; }`. Such a
  compute needn't record a dependency. A violation still throws. Callers waiting on the same key
  don't share the value: FusionCache hands a waiter only what was stored, so each runs its own
  compute once the first ends.
- **New: `Query.Size`,** on both query types: the entry's size in Sluice's memory cache, for an
  app that gives that cache a `SizeLimit` through the FusionCache builder. FusionCache lets
  Sluice pin its own tag and clear markers, so `AddSluice` now forces
  `TagsDefaultEntryOptions.Priority = NeverRemove` and `Size = 0` (FusionCache's own defaults):
  a marker compacted away, or refused when the cache is full, would let an invalidated value be
  served again. They aren't counted against the limit.
- **Performance:** a save now reports one more value per unique column of each changed row,
  which is two more tags to invalidate. On the EF benchmark (in-memory SQLite, an item with one
  unique column), saving one changed row with Sluice went from 14.9 µs and 12.2 KB to 15.8 µs and
  15.4 KB; without Sluice it's 10.2 µs either way. Cache misses are unchanged within noise
  (`read.Entity` 29.7 → 30.3 µs, a plain query 15.5 → 15.9 µs). A type without unique columns
  pays nothing more.
- **Fixed: a foreign key its value converter stores as null.** A save of such a row threw while
  listing what to invalidate, and lost the save's invalidation; it now treats the key as null,
  as the database does. `read.Children` with such a key throws `ArgumentException` (it threw
  `InvalidOperationException`). A foreign key whose converter stores null as a value
  (`convertsNulls`) now makes `read.Children` throw `NotSupportedException`: a row with no
  parent holds that value, and no save evicted its readers.

## 0.3.0 — breaking

EF Core reads inside a compute are ordinary queries now. Sluice records the entity types each
one reads (by class, not table), instead of refusing everything but its helpers.

- **Breaking: upgrade `Meridian.Sluice` and `Meridian.Sluice.EntityFrameworkCore` together.**
  The EF package 0.2.0 calls a hidden core member that 0.3.0 removed, so mixing the two versions
  fails at run time with `MissingMethodException`. From 0.3.0 the EF package depends on exactly
  its own version of the core (`[0.3.0]`, not `>= 0.3.0`): NuGet fails the restore when your
  project asks for an older core, but only warns (NU1608) when it asks for a newer one. Don't
  reference `Meridian.Sluice` yourself alongside the EF package, or treat NU1608 as an error.
- **New: `read.Db<TContext>()`.** A compute queries a context of its own, taken from a DI scope
  the compute opens for itself and disposed with it when the compute ends. It tracks nothing by
  default and isn't in the caller's transaction, so it can't cache a request's unsaved edits or
  uncommitted rows. Any EF registration of the context works (`AddDbContext`,
  `AddDbContextFactory`, `AddDbContextPool`, `AddPooledDbContextFactory`), and its constructor
  may take scoped services; with a factory, register it `ServiceLifetime.Scoped` for that. The
  scope's services are made on the flow of whichever caller computes first, so a tenant provider
  over `IHttpContextAccessor` gives that caller's tenant: set the tenant with a configure, below.
  `read.Db` resolves the context itself from DI, so register it, not only a factory of your own.
  Inside a compute, EF commands on any other context throw, the read helpers included; pass
  them `read.Db<TContext>()`'s sets. Raw ADO.NET or Dapper on its connection outside a fused
  fetch isn't tracked.
- **New: `read.Db<TContext>(configure)`, for tenants.** It sets the compute's context up from the
  cache key before its first query: the tenant its query filters read
  (`read.Db<AppDb>(db => db.TenantId = tenantId)`), or a tenant's database
  (`db.Database.SetConnectionString(...)`). A query filter that reads a settable field or
  auto-property of the context runs only on a context the compute configured, and such a query
  carries a third SQL comment, `-- Sluice reads the context's state: …`. Sluice takes on trust
  that the configure sets it from the key. Only the compute's first `read.Db<TContext>` may
  configure; a later configure throws. A pooled context keeps what the configure set into its
  next lease, so set the tenant on every lease, as EF's docs show.
- **Plain EF queries work inside a compute,** with `Include`, navigations, joins, subqueries,
  set operations, many-to-many navigations, `Find`, explicit loading and compiled queries. Each
  query records every entity type it reads, query filters and auto-includes included, and a write
  to any of them evicts the value. The first time each cached query reads a whole type, Sluice
  logs it at `Information`, so you can see what to narrow.
- **`read.Entity` and `read.Children` narrow a query** to one row or one parent's rows. Their
  queries may now follow navigations and `Include`, which record the types they reach. They turn
  off auto-includes for the whole query, so what you `Include` loads without its own
  auto-included navigations. `read.Children` never takes a null key.
- **Removed: `read.All`.** A plain query on `read.Db`'s context does the same, except that it
  also loads auto-included navigations (ones the model includes in every query); add
  `.IgnoreAutoIncludes()` to get `read.All`'s shape.
- **Every query on a `UseSluice` context now starts with a SQL comment,** inside a compute or
  not: `-- Sluice reads: …` naming the entity types it reads, or `-- Sluice can't track: …` with
  the reason, and `-- Sluice reads the context's state: …` when its filters read the context.
  You'll see them in logs, `ToQueryString()` and SQL snapshot tests.
- **Still refused inside a compute:** raw SQL, views, keyless types, your own database functions,
  query filters that read the context's state on a context the compute didn't configure, or
  where the configure can't set it (through a service, a property with a body, a method, a
  readonly field, or an init-only or get-only property), entities that load lazily or hold
  their context, writes, and reads inside a transaction. Read the first four in a fused
  `read.From` fetch; declare it with the sets' `AllDependencies()` and EF writes evict it.
- **Newly refused inside a compute:** a query filter that reads state that can change, such as a
  static tenant accessor, a captured service or `DateTime.Now`, since its value differs per
  call; and another compute's `read` (its `read.From`, `read.Db` or a helper), captured and used
  in this one, which would record what this compute read on the other.
- **The refusal of a query filter names it.** The message names each refused filter by its type
  and filter name, and says to ignore just those (`IgnoreQueryFilters(["Tenant"])`), not
  `IgnoreQueryFilters()`, which drops every filter, soft delete included. For a filter with no
  name, it says to name it first.
- **New: `ExecuteUpdate` and `ExecuteDelete` invalidate by themselves,** once they commit: every
  read of the types stored in the table they wrote, and for a delete what the database cascades
  into. One that fails or is cancelled still invalidates, as a save does; one that changes no
  rows invalidates nothing. Delete any invalidation you wrote by hand after them.
- **Changed: without a row version, updating or deleting a row evicts its `Children` lists
  under every parent.** The parent EF loaded may not be the one the row leaves, if another
  writer moved it since, which left that parent's list stale. Map a concurrency token the
  database maintains (`xmin` with `IsRowVersion()` on PostgreSQL, `rowversion` on SQL Server),
  or make the foreign key a concurrency token, to keep eviction exact; EF then refuses a save on
  a row that changed since it was loaded. A type split across tables (table-per-type, entity
  splitting) always falls back. Sluice logs once per type and cache at `Information` (events 3
  and 4) when it falls back. Inserts, and foreign keys in the primary key (such as a join
  row's), stay exact.
- **Fixed: deleting a derived row through a base-type stub** (`Remove(new Vehicle { Id = id })`
  for a truck) now evicts the `Children` lists under the derived type's own foreign keys.
- **Behaviour change: a compute runs outside the caller's ambient transaction.** Inside a
  `TransactionScope`, connections a compute opens don't join it, so computes read what's
  committed. That applies to Dapper and other non-EF computes too: one called inside a
  `TransactionScope` no longer sees that scope's uncommitted rows.
- **New: `Query<TValue>`,** a query with no key.
- **`UseSluice` is safe to call twice** for one context; EF composes a pool's and a factory's
  configuration, which used to make a save publish before its commit.
- **Breaking: calling `AddSluice` twice on one service collection throws**
  `InvalidOperationException`, saying to call it once.
- **Clearer errors:** `UseSluice` without `AddSluice`, `read.Db` with no context registered (or
  one the scope can't make, such as scoped dependencies from a singleton factory) or without
  `UseSluice`, and a compute that recorded nothing all say what to do. For a value with nothing
  to depend on, the last now says how to get an `IFusionCache`: register one of your own with
  `services.AddFusionCache()`, since Sluice's is a named cache.
- **Docs:** the "Strict Reads" page is now "What a compute can't read", at the same address.
  Limitations gains: two classes mapped to one table aren't linked; moving or renaming an entity
  class changes its dependencies during a rolling deploy; and a FusionCache 2.9 race can lose
  an invalidation on one server. The Redis sample has its `using` lines.

## 0.2.0 — breaking

Sluice is rewritten from scratch as a thin layer over FusionCache. Nothing from 0.1 survives:
code written against it won't compile.

- **Removed:** the hand-rolled cache store, dependency graph, stampede coordination, epoch fence,
  event sink and `Describe`/`DumpGraph` observability; `SluiceKernel`, `ISluice`, `IReadScope`,
  `EntityResource`, `CollectionResource`, `TrackedRead`, `TrackedWrite`, `WriteEffect`,
  `CachedQuery` and the `IResourceKey` marker interface; the `Sluice.Redis` package; the source
  generator and its attributes.
- **Caching is FusionCache's.** `AddSluice(cache => …)` registers `SluiceCache` over a FusionCache
  of its own and passes you FusionCache's builder, so memory and distributed levels, the
  backplane, stampede protection, fail-safe and serialisation are configured there. Recorded
  dependencies become FusionCache tags, and invalidation is `RemoveByTag`.
- **Declarations:** `Source<TKey>` replaces `EntityResource`/`CollectionResource` (`For(key)` and
  `All`), and `Query<TKey, TValue>` replaces `CachedQuery`, without a compute or a version inside
  it. Keys need no marker interface: `string` and `IFormattable` keys work as they are, and any
  other key type passes a `format` on its declaration.
- **Reading:** `cache.GetOrSetAsync(query, key, async (read, ct) => …)`. `read.From(dependency,
  fetch)` records a dependency and fetches in one call, replacing `TrackedRead`; record-only
  `read.From(dependencies)` covers the rest. A compute that records nothing throws.
- **Writing:** `cache.InvalidateAsync(dependencies)` after the write commits, replacing
  `TrackedWrite` and write effects. `ClearAsync` replaces `FlushAllAsync`.
- **New: `Sluice.EntityFrameworkCore`.** `UseSluice` on a context invalidates tracked writes once
  they commit, with no Sluice code at the call site. `read.Entity`, `read.Children` and
  `read.All` take their dependencies from the EF model, and EF reads inside a compute are strict.
  `set.Dependency(key)` and `set.AllDependencies()` cover writes the change tracker can't see.
- **Database cascades evict whole types.** When a delete lets the database delete (`Cascade`)
  or null (`SetNull`) rows of other types, Sluice can't see which rows it touched. So it evicts
  every `Entity` and `All` read of each type reached, at any depth, and the `Children` reads
  through the foreign keys the cascade crossed and the other foreign keys of the rows it
  deleted or nulled.
  Deleting one customer evicts every cached `Entity` and `All` read of orders.
  `set.AllDependencies()` includes the same, so it covers an `ExecuteDelete` of parents.
- **New package ids:** `Meridian.Sluice` and `Meridian.Sluice.EntityFrameworkCore`, because the
  `Sluice` id on NuGet belongs to another project. Namespaces stay `Sluice`.
- Both packages target .NET 10 and share one version.
