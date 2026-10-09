# Changelog

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
