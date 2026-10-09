# Limitations

These are Sluice's deliberate limits, listed here so you don't discover them in production.
Each links to the page that explains it. Where a limit can leave an old value cached, it lives
no longer than the entry's duration.

## Writes Sluice can't see

- **Anything that writes without a `UseSluice` context must invalidate by hand**: another
  service, a cron job, SQL run by hand, a database trigger, a second context without
  `UseSluice`, `ExecuteUpdate`, `ExecuteDelete` and raw SQL. Otherwise the old value stays
  cached until it expires, so keep durations short for data written outside your app. See
  [Writes Sluice can't see](/guides/ef-core#writes-sluice-can-t-see).
- **On the generic path, every write is yours to report**, after it commits. Before the commit,
  or never, and the old value can stay until it expires. See
  [After the commit, never before](/guides/invalidation#after-the-commit-never-before).

## Everywhere

- **A crash between commit and invalidation loses the invalidation.** Nothing replays it after
  a restart. See
  [Transactions](/guides/transactions#what-s-not-covered).
- **Computes must read committed data from the primary database**, not inside your own open
  transaction or from a replica that may lag behind. See
  [Computes and transactions](/guides/transactions#computes-and-transactions).
- **Whatever changes what a compute reads belongs in the query key**: a tenant, a locale, a
  soft-delete switch. See [Tenants](/guides/strict-reads#tenants-and-other-context-state).
- **Cached values are shared instances**, entities included, unless you turn on auto-clone. See
  [Shared instances](/guides/configuration#shared-instances-and-auto-clone).
- **No keyless queries.** Key a global value by something real, such as a tenant or a locale.
  See [Queries](/guides/sources-and-queries#queries).
- **Your `format` functions and `IFormattable` ids must give different text for different
  keys.** Sluice can't check that. See [Key text](/guides/sources-and-queries#key-text).
- **Source names aren't checked for duplicates**, and query names only within one process. See
  [Names](/guides/sources-and-queries#names) and [Queries](/guides/sources-and-queries#queries).
- **Renaming a source or query, or changing a `format`, changes its cache keys.** During a
  rolling deploy, old and new servers don't evict each other's entries. See
  [Renaming changes the cache keys](/guides/sources-and-queries#renaming-changes-the-cache-keys).
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
  other types, or set their foreign key to null, every `Entity` and `All` read of those types is
  evicted, whichever rows it hit. See
  [Database cascades](/guides/ef-core#database-cascades).
- **Cascades the model doesn't know about** (triggers, a database that differs from the model)
  aren't seen at all.
  See [Database cascades](/guides/ef-core#database-cascades).
- **A row saved from an object you built yourself** misses its real old parent when its foreign
  key differs from the stored one. See
  [Rows saved without loading them](/guides/ef-core#rows-saved-without-loading-them).
- **Two entity types splitting one table and sharing a column**: saving one doesn't evict the
  other's readers. See [Model shapes](/guides/ef-core#model-shapes).
- **Broad eviction, by design**: a `Children` list is evicted by a change to any row in it, and
  an `All` read by any write to the type. See [Writing](/guides/ef-core#writing).
- **Commits Sluice doesn't see**: a raw `DbTransaction` committed outside EF, and some
  `TransactionScope` setups. See [Transactions](/guides/transactions#ef-core).
- **Interceptors that change entities in `SavingChanges`** must come before `UseSluice`. See
  [Interceptor order](/guides/ef-core#interceptor-order).
- **Relational providers only, and no `UseInternalServiceProvider`.** See
  [Setup](/guides/ef-core#setup).
- **Tested on PostgreSQL only.** Key folding assumes PostgreSQL's comparison rules, and doesn't
  fold accent- or culture-sensitive collations. See [Key folding](/guides/ef-core#key-folding).
- **Composite primary keys can't be read with `read.Entity`**, and `read.Children` takes a
  single-column foreign key. See [Keys](/guides/ef-core#keys).
- **Skip navigations, views, keyless types, functions and SQL-query mappings** can't be read
  through the helpers. See [What's refused](/guides/ef-core#what-s-refused).

## Strict reads

- **Some reads go unchecked inside a compute**, such as `Find` of a tracked entity or Dapper on
  the context's connection. See [What isn't checked](/guides/strict-reads#what-isn-t-checked).
- **Some reads only work in a fused fetch**: `FromSql`, `SqlQuery`, compiled queries,
  navigations and lazy loading. See [The declared route](/guides/strict-reads#the-declared-route).
- **Writes can't run inside a compute.** See
  [Writes are refused](/guides/strict-reads#writes-are-refused).
- **Some harmless queries are refused**, such as `order.Customer!.Id`. See
  [A helper's query reads only its own rows](/guides/strict-reads#a-helper-s-query-reads-only-its-own-rows).
