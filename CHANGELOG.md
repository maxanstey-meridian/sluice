# Changelog

## 0.4.0

Three gaps found while planning Casebridge's first cached reads: a tenant filter that reads a
scoped service, a lookup by a unique column, and keys from public input.

- **New: `read.Db<TContext>((db, services) => …)`.** The configure gets the compute's own DI
  scope too, the one its context was made in, so it can set a scoped service the context reads:
  `read.Db<AppDb>((db, services) => services.GetRequiredService<TenantProvider>().Set(tenantId))`.
  It runs where `read.Db<TContext>(db => …)` runs (once, on the compute's first
  `read.Db<TContext>`, before any query) and fails the same way. The `db => …` overload stays.
- **New: `[SluiceContextState]`, for a query filter that reads a scoped tenant service.** Mark a
  property of the context whose body reads only what the compute's configure sets
  (`[SluiceContextState] private Guid? TenantId => _tenant.Id;`) and a filter may read it inside
  a compute, exactly as it may read a settable field: only on a configured context, with the
  `-- Sluice reads the context's state: …` comment. Sluice doesn't read the body; the attribute is
  your promise, as the configure is. It must hold a plain value and not be virtual, unless
  sealed; it may be private. Unmarked properties with a body, methods and members of objects the
  context holds are still refused, and the refusal of an unmarked context property now says to
  mark it.
- **New: `read.Entity(set, column, value)`, by a unique column.** A lookup by slug or email
  depends on the row holding that value, not on every row of the type: a save of another row
  doesn't evict it; one that gives a row the value, changes the row holding it, or moves the
  value off it, does, so a cached miss is evicted once a row gets the value. The column must be
  covered by a single-column unique index (a filtered one counts) or alternate key in the EF
  model; anything else throws `ArgumentException`, a composite one `NotSupportedException`.
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
