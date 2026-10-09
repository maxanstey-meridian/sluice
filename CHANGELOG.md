# Changelog

## 0.3.0 — breaking

EF Core reads inside a compute are ordinary queries now. Sluice records the tables each one
reads, instead of refusing everything but its helpers.

- **New: `read.Db<TContext>()`.** A compute queries a context of its own, made from the
  registered `IDbContextFactory<TContext>` and disposed when the compute ends. It tracks nothing
  by default and isn't in the caller's transaction, so it can't cache a request's unsaved edits
  or uncommitted rows. **Breaking:** register the context with `AddDbContextFactory` (or
  `AddPooledDbContextFactory`), which also registers the context itself. Inside a compute, EF
  commands on any other context throw, the read helpers included; pass them
  `read.Db<TContext>()`'s sets.
- **Plain EF queries work inside a compute,** with `Include`, navigations, joins, subqueries,
  set operations, many-to-many navigations, `Find`, explicit loading and compiled queries. Each
  query records every entity type it reads, query filters and auto-includes included, and a write
  to any of them evicts the value. The first time each cached query reads a whole type, Sluice
  logs it at `Information`, so you can see what to narrow.
- **`read.Entity` and `read.Children` narrow a query** to one row or one parent's rows. Their
  queries may now follow navigations and `Include`, which record the types they reach.
- **Removed: `read.All`.** A plain query on `read.Db`'s context does the same.
- **Still refused inside a compute:** raw SQL, views, keyless types, your own database functions,
  query filters that read the context's state, entities that load lazily or hold their context,
  writes, and reads inside a transaction. Read the first four in a fused `read.From` fetch.
- **New: `ExecuteUpdate` and `ExecuteDelete` invalidate by themselves,** once they commit: every
  read of the table they wrote, and for a delete what the database cascades into.
- **New: a compute runs outside the caller's ambient transaction.**
- **New: `Query<TValue>`,** a query with no key.
- **`UseSluice` is safe to call twice** for one context; EF composes a pool's and a factory's
  configuration, which used to make a save publish before its commit.
- **Clearer errors:** `AddSluice` called twice, `UseSluice` without `AddSluice`, `read.Db`
  without a factory or without `UseSluice`, and a compute that recorded nothing all say what to
  do.

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
