# Limitations

These are Sluice's deliberate limits, listed here so you don't discover them in production.
Each links to the page that explains it. Where a limit can leave an old value cached, it lives
no longer than the entry's duration.

## Writes Sluice can't see

- **Anything that writes without a `UseSluice` context must invalidate by hand**: another
  service, a cron job, SQL run by hand, a database trigger, a context without `UseSluice`, and
  `ExecuteSql`. Otherwise the old value stays cached until it expires, so keep durations short
  for data written outside your app. See
  [Writes Sluice can't see](/guides/ef-core#writes-sluice-can-t-see).
- **On the generic path, every write is yours to report**, after it commits. Before the commit,
  or never, and the old value can stay until it expires. See
  [After the commit, never before](/guides/invalidation#after-the-commit-never-before).

## Everywhere

- **A crash between commit and invalidation loses the invalidation.** Nothing replays it after
  a restart. See
  [Transactions](/guides/transactions#what-s-not-covered).
- **A FusionCache 2.9 race can lose an invalidation on one server.** An invalidation that lands
  in the same microseconds as a read checking that dependency can be overwritten by the check,
  and the stale value is then served on that server until the dependency is invalidated again.
  The window opens when a server first checks a dependency, and again about once an hour after
  that. It's a FusionCache bug; Sluice has no workaround.
- **Computes must read committed data from the primary database**, not inside your own open
  transaction or from a replica that may lag behind. See
  [Computes and transactions](/guides/transactions#computes-and-transactions).
- **Whatever changes what a compute reads belongs in the query key**: a tenant, a locale, a
  soft-delete switch. See [Tenants](/guides/strict-reads#tenants-and-other-context-state).
- **Cached values are shared instances**, entities included, unless you turn on auto-clone. See
  [Shared instances](/guides/configuration#shared-instances-and-auto-clone).
- **Your `format` functions and `IFormattable` ids must give different text for different
  keys.** Sluice can't check that. See [Key text](/guides/sources-and-queries#key-text).
- **Source names aren't checked for duplicates**, and query names only within one process. See
  [Names](/guides/sources-and-queries#names) and [Queries](/guides/sources-and-queries#queries).
- **Renaming a source or query, or changing a `format`, changes its cache keys.** During a
  rolling deploy, old and new servers don't evict each other's entries. See
  [Renaming changes the cache keys](/guides/sources-and-queries#renaming-changes-the-cache-keys).
- **Moving or renaming an EF entity class changes its dependencies' names**, which are the
  class's full name (`Shop.Domain.Order`). During a rolling deploy, old and new servers don't
  evict each other's cached reads of that type until those entries expire. See
  [What a query records](/guides/ef-core#what-a-query-records).
- **A caller waiting on another caller's compute can't cancel the wait.** See
  [Cancellation](/guides/invalidation#cancellation).
- **FusionCache's factory timeouts and its per-key `DefaultEntryOptionsProvider` don't apply**
  to Sluice calls. See [What Sluice sets](/guides/configuration#what-sluice-sets).
- **Work started under `ExecutionContext.SuppressFlow`** can't see the compute, so the nesting
  and strict-read guards are off there. See
  [What isn't checked](/guides/strict-reads#what-isn-t-checked).

## Multi-node

- **Clocks that disagree between servers** open a window about as wide as the difference:
  FusionCache compares when one server stored an entry with when another invalidated it.
- **A missed backplane message** leaves that server serving old values until its in-memory tag
  data expires (an hour by default). Without a distributed cache, only the entries' own expiry
  ends it.
- **A Redis blip while a server first looks a tag up** can make it treat the tag as never
  invalidated until that lookup expires, even if it missed no message.
- **A slow Redis delays every invalidation, and every EF save**, until the Redis client times
  out. See [When Redis is slow or down](/guides/configuration#when-redis-is-slow-or-down).

## EF Core

- **A database cascade evicts whole types.** When a delete makes the database delete rows of
  other types, or set their foreign key to null, every `Entity` read and plain query of those
  types is evicted, whichever rows it hit. See
  [Database cascades](/guides/ef-core#database-cascades).
- **Cascades the model doesn't know about** (triggers, a database that differs from the model)
  aren't seen at all. See [Database cascades](/guides/ef-core#database-cascades).
- **Without a row version, updating or deleting a row evicts its `Children` lists under every
  parent**, since the parent EF loaded may not be the one the row leaves. Map a row version
  (`xmin` on PostgreSQL, `rowversion` on SQL Server) to keep that exact. See
  [Row versions](/guides/ef-core#row-versions).
- **A row saved from an object you built yourself**, of a type with a row version, misses its
  real old parent when its foreign key differs from the stored one. See
  [Rows saved without loading them](/guides/ef-core#rows-saved-without-loading-them).
- **Two entity types splitting one table and sharing a column**: saving one doesn't evict the
  other's readers. See [Model shapes](/guides/ef-core#model-shapes).
- **Two classes mapped to the same table aren't linked.** Dependencies belong to entity
  types (classes), not tables. A second class over the same table, such as a read model in
  another context, is a separate type: a write through either doesn't evict reads through the
  other, both ways. To link them, have each one's reads also record the other's
  `AllDependencies()` with `read.From`, or invalidate by hand. See
  [What a query records](/guides/ef-core#what-a-query-records).
- **Broad eviction, by design**: a plain query is evicted by any write to any type it reads, a
  `Children` list by a change to any row in it, and every read of a type by an `ExecuteUpdate`
  or `ExecuteDelete` on its table. See
  [What a query records](/guides/ef-core#what-a-query-records) and [Bulk updates and deletes](/guides/ef-core#bulk-updates-and-deletes).
- **Commits Sluice doesn't see**: a raw `DbTransaction` committed outside EF, and some
  `TransactionScope` setups. See [Transactions](/guides/transactions#ef-core).
- **Interceptors that change entities in `SavingChanges`** must come before `UseSluice`. See
  [Interceptor order](/guides/ef-core#interceptor-order).
- **Relational providers only, and no `UseInternalServiceProvider`.** Computes take the context
  from a DI scope of their own, so it must be registered as a scoped service (every EF
  registration does that). See [Setup](/guides/ef-core#setup).
- **Tested on PostgreSQL only.** Key folding assumes PostgreSQL's comparison rules, and doesn't
  fold accent- or culture-sensitive collations. See [Key folding](/guides/ef-core#key-folding).
- **Composite primary keys can't be read with `read.Entity`**, and `read.Children` takes a
  single-column foreign key. See [Keys](/guides/ef-core#keys).
- **Views, keyless types, functions and SQL-query mappings** can't be read inside a compute
  except in a fused fetch. See [What's refused](/guides/ef-core#what-s-refused).

## What a compute can't read

- **Some reads go unchecked inside a compute**, such as `Find` of an entity a captured outside
  context already tracks, or Dapper on a context's connection. See
  [What isn't checked](/guides/strict-reads#what-isn-t-checked).
- **Some reads only work in a fused fetch**: raw SQL, views, keyless types and your own
  database functions. See [The declared route](/guides/strict-reads#the-declared-route).
- **Query filters that read the context's state need the compute to set it**, from the query
  key, with `read.Db<AppDb>(db => db.TenantId = tenantId)`; Sluice can't check what the
  configure sets. Filters that read it through a service the context holds are refused. See
  [Tenants](/guides/strict-reads#tenants-and-other-context-state).
- **Invalidation isn't per tenant.** A write in one tenant evicts other tenants' cached reads
  of the same table, unless they read through `read.Children` on a tenant foreign key.
- **Entities that load lazily or hold their context can't be returned by a query in a
  compute.** Select the values you need. See
  [What throws inside a compute](/guides/strict-reads#what-throws-inside-a-compute).
- **Writes can't run inside a compute.** See [Writes](/guides/strict-reads#writes).
