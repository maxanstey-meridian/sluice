# Changelog

## 0.3.0 — breaking

EF Core reads inside a compute are ordinary queries now. Sluice records the entity types each
one reads, instead of refusing everything but its helpers.

- **Breaking: upgrade `Meridian.Sluice` and `Meridian.Sluice.EntityFrameworkCore` together.**
  The EF package 0.2.0 calls a hidden core member that 0.3.0 removed, so mixing the two versions
  fails at run time with `MissingMethodException`. From 0.3.0 the EF package depends on exactly
  its own version of the core (`[0.3.0]`, not `>= 0.3.0`), so NuGet refuses a mismatched pair
  when it restores.
- **New: `read.Db<TContext>()`.** A compute queries a context of its own, made in a DI scope
  the compute opens for itself and disposed with it when the compute ends. It tracks nothing by
  default and isn't in the caller's transaction, so it can't cache a request's unsaved edits or
  uncommitted rows. Any EF registration of the context works (`AddDbContext`,
  `AddDbContextFactory`, `AddDbContextPool`, `AddPooledDbContextFactory`), and its constructor
  may take scoped services; with a factory, register it `ServiceLifetime.Scoped` for that.
  **Breaking:** a context registered only through an `IDbContextFactory<TContext>` of your own,
  without the context itself, throws. Inside a compute, EF commands on any other context throw,
  the read helpers included; pass them `read.Db<TContext>()`'s sets.
- **New: `read.Db<TContext>(configure)`, for tenants.** It sets the compute's context up from the
  cache key before its first query: the tenant its query filters read
  (`read.Db<AppDb>(db => db.TenantId = tenantId)`), or a tenant's database
  (`db.Database.SetConnectionString(...)`). A query filter that reads a field or auto-property of
  the context runs only on a context the compute configured; Sluice takes on trust that the
  configure sets it from the key. Only the compute's first `read.Db<TContext>` may configure;
  a later configure throws. A pooled context keeps what the configure set into its next lease,
  so set the tenant on every lease, as EF's docs show.
- **Plain EF queries work inside a compute,** with `Include`, navigations, joins, subqueries,
  set operations, many-to-many navigations, `Find`, explicit loading and compiled queries. Each
  query records every entity type it reads, query filters and auto-includes included, and a write
  to any of them evicts the value. The first time each cached query reads a whole type, Sluice
  logs it at `Information`, so you can see what to narrow.
- **`read.Entity` and `read.Children` narrow a query** to one row or one parent's rows. Their
  queries may now follow navigations and `Include`, which record the types they reach.
- **Removed: `read.All`.** A plain query on `read.Db`'s context does the same, except that it
  also loads auto-included navigations (ones the model includes in every query); add
  `.IgnoreAutoIncludes()` to get `read.All`'s shape.
- **Every query on a `UseSluice` context now starts with a SQL comment,** inside a compute or
  not: `-- Sluice reads: …` naming the entity types it reads, or `-- Sluice can't track: …` with
  the reason. You'll see it in logs, `ToQueryString()` and SQL snapshot tests.
- **Still refused inside a compute:** raw SQL, views, keyless types, your own database functions,
  query filters that read the context's state on a context the compute didn't configure, or
  through a service, a property with a body or a method, entities that load lazily or hold their
  context, writes, and reads inside a transaction. Read the first four in a fused `read.From`
  fetch.
- **Newly refused inside a compute:** a query filter that reads state that can change, such as a
  static tenant accessor, a captured service or `DateTime.Now`, since its value differs per
  call; and another compute's `read` (its `read.From`, `read.Db` or a helper), captured and used
  in this one, which would record what this compute read on the other.
- **The refusal of a query filter names it.** When a query filter reads the context's or other
  shared state, the message names each refused filter by its type and filter name, and says to
  ignore just those (`IgnoreQueryFilters(["Tenant"])`), not `IgnoreQueryFilters()`, which drops
  every filter, soft delete included. For a filter with no name, it says to name it first. The
  Tenants section of "What a compute can't read" shows the named form.
- **New: `ExecuteUpdate` and `ExecuteDelete` invalidate by themselves,** once they commit: every
  read of the types stored in the table they wrote, and for a delete what the database cascades
  into. One that fails or is cancelled still invalidates, as a save does; one that changes no
  rows invalidates nothing. Delete any invalidation you wrote by hand after them.
- **Changed: without a row version, updating or deleting a row evicts its `Children` lists
  under every parent.** The parent EF loaded may not be the one the row leaves, if another
  writer moved it since, which left that parent's list stale. Map a concurrency token the
  database maintains (`xmin` with `IsRowVersion()` on PostgreSQL, `rowversion` on SQL Server)
  to keep eviction exact; EF then refuses a save on a row that changed since it was loaded.
  Sluice logs once per type at `Information` when it falls back. Inserts, join rows and
  foreign keys in the primary key stay exact.
- **Behaviour change: a compute runs outside the caller's ambient transaction.** Inside a
  `TransactionScope`, connections a compute opens don't join it, so computes read what's
  committed. That applies to Dapper and other non-EF computes too: one called inside a
  `TransactionScope` no longer sees that scope's uncommitted rows.
- **New: `Query<TValue>`,** a query with no key.
- **`UseSluice` is safe to call twice** for one context; EF composes a pool's and a factory's
  configuration, which used to make a save publish before its commit.
- **Breaking: calling `AddSluice` twice on one service collection throws**
  `InvalidOperationException`, saying to call it once.
- **Clearer errors:** `UseSluice` without `AddSluice`, `read.Db` without a factory or without
  `UseSluice`, and a compute that recorded nothing all say what to do. For a value with nothing
  to depend on, the last now says how to get an `IFusionCache`: register one of your own with
  `services.AddFusionCache()`, since Sluice's is a named cache.
- **Dependencies belong to entity types, not tables.** The docs said a query records the tables
  it reads; it records the classes, by full name. So a second class mapped to the same table,
  such as a read model in another context, isn't linked to the first, and moving or renaming an
  entity class changes its dependencies, so old and new servers don't evict each other's reads
  of it during a rolling deploy. Both are now in Limitations.
- **Docs:** the "Strict Reads" page is now "What a compute can't read", at the same address. It
  and the EF Core page show how to read a view, a keyless type or Dapper in a fused fetch
  declared with the sets' `AllDependencies()`, so EF writes evict it with no invalidation by
  hand. The helpers' auto-include warning now says what you `Include` loses its auto-included
  navigations too, with the fix. `read.Children` takes a nullable foreign key property but never
  a null key, which throws `ArgumentNullException`. The README lists Dapper on `read.Db`'s
  connection as untracked, and the Redis sample has its `using` lines.

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
