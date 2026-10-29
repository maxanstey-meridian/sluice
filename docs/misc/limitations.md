# Limitations

These are Sluice's deliberate limits, listed here so you don't discover them in production.
Each links to the page that explains it. Where a limit can leave an old value cached, it lives
no longer than the entry's duration, or its query's `MaxStaleness` if that's longer.

## Writes Sluice can't see

- **Anything that writes without a `UseSluice` context must invalidate by hand**: another
  service, a cron job, SQL run by hand, a database trigger, a context without `UseSluice`, and
  `ExecuteSql`. Otherwise the old value stays cached until it expires, so keep durations short
  for data written outside your app. See
  [Writes Sluice can't see](/guides/ef-core#writes-sluice-can-t-see).
- **On the generic path, every write is yours to report**, after it commits. Before the commit,
  or never, and the old value can stay until it expires. A change made outside your code, such
  as a flag flipped in a vendor's dashboard, needs a webhook or a poll that invalidates it. See
  [Keep the invalidation with the write](/guides/invalidation#keep-the-invalidation-with-the-write)
  and [After the commit, never before](/guides/invalidation#after-the-commit-never-before).

## Everywhere

- **A crash between commit and invalidation loses the invalidation**, and the old value lives
  until its entry expires. That's a deliberate trade-off: Sluice keeps no outbox of pending
  invalidations, so the entry's duration bounds how long a lost one lasts. An app that needs
  more records what it wrote in an outbox of its own, in the same transaction, and calls
  `InvalidateAsync` from the outbox's handler. See
  [Transactions](/guides/transactions#what-s-not-covered).
- **A FusionCache 2.9 race can delay an invalidation on one server by up to 6 s.** An
  invalidation that lands in the same microseconds as a read checking that dependency can be
  overwritten by the check (FusionCache#659), and that server keeps serving the values cached
  before it. Sluice publishes every invalidation and clear again 5 to 6 s later, which restores it;
  it stays lost only if that server's Redis lookup of the tag takes longer than 5 s (the
  [recommended Redis timeouts](/guides/configuration#multi-node) of one second keep lookups well
  under that). See
  [What Sluice sets](/guides/configuration#what-sluice-sets).
- **Computes must read committed data from the primary database**, not inside your own open
  transaction or from a replica that may lag behind. See
  [Computes and transactions](/guides/transactions#computes-and-transactions).
- **Whatever changes what a compute reads belongs in the query key**: a tenant, a locale, a
  soft-delete switch. See [Tenants](/guides/strict-reads#tenants-and-other-context-state).
- **Cached values are shared instances**, entities included, unless you turn on auto-clone. See
  [Shared instances](/guides/configuration#shared-instances-and-auto-clone).
- **Your `format` functions and `IFormattable` ids must give different text for different
  keys.** Sluice can't check that. See [Key text](/guides/sources-and-queries#key-text).
- **Query and source names are checked only within one process**, and two declarations'
  formats only by the method they compile to: one lambda that formats differently depending on
  what it captured, and a format compiled from an expression, aren't caught. See
  [Names](/guides/sources-and-queries#names) and [Queries](/guides/sources-and-queries#queries).
- **Renaming a source or query, or changing a `format`, changes its cache keys**, and so does
  renaming a table, or its schema, that EF maps. During a rolling deploy, old and new servers
  don't evict each other's entries. See
  [Renaming changes the cache keys](/guides/sources-and-queries#renaming-changes-the-cache-keys).
- **A caller waiting on another caller's compute can't cancel the wait.** See
  [Cancellation](/guides/invalidation#cancellation).
- **FusionCache's factory timeouts and its per-key `DefaultEntryOptionsProvider` don't apply**
  to Sluice calls. See [What Sluice sets](/guides/configuration#what-sluice-sets).
- **Work started under `ExecutionContext.SuppressFlow`** can't see the compute, so the nesting
  and read checks are off there. See
  [What isn't checked](/guides/strict-reads#what-isn-t-checked).
- **Callers waiting on a `read.DoNotCache()` compute don't share its value.** Each runs its own
  compute once the first ends, one at a time, since FusionCache hands a waiter only what was
  stored. See [Leaving a value uncached](/guides/sources-and-queries#leaving-a-value-uncached).
- **Under a memory size limit, tag markers aren't counted.** Sluice pins them (size 0,
  `NeverRemove`) so an invalidation is never lost to compaction, so the cache can hold more than
  the limit by one small entry per dependency (twice that for one read by a query with
  `MaxStaleness`). See [Limiting memory](/guides/configuration#limiting-memory).

## Multi-node

- **Clocks that disagree between servers** open a window about as wide as the difference:
  FusionCache compares when one server stored an entry with when another invalidated it.
- **A stale copy expires on each server's own clock.** A query with `MaxStaleness` serves a
  written value until that long after its compute began, as the reading server tells the time,
  so clocks that disagree shift the bound by the difference. See
  [MaxStaleness](/guides/sources-and-queries#maxstaleness).
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
  parent, and its `read.EntityAsync` readers by unique column for every value**, since the parent or
  value EF loaded may not be the one the row leaves. Map a row version (`xmin` on PostgreSQL,
  `rowversion` on SQL Server) to keep that exact. See
  [Row versions](/guides/ef-core#row-versions).
- **Reading by a unique column needs the model to know it's unique**: a single-column unique
  index (filtered or not) or alternate key. Composite unique indexes, and owned types' columns,
  can't be read by value. A unique column or foreign key whose value converter stores null as a
  value (EF's internal `convertsNulls`) is refused with `NotSupportedException`.
  `set.Dependency(key)` evicts readers by a unique column that found that row, but not a
  cached "not found"; use `AllDependencies()` after a write Sluice can't see that may have given
  a row the value. See [By a unique column](/guides/ef-core#by-a-unique-column) and
  [Key folding](/guides/ef-core#key-folding).
- **A row saved from an object you built yourself**, of a type with a row version, misses its
  real old parent when its foreign key differs from the stored one, and its real old value of a
  unique column likewise. See
  [Rows saved without loading them](/guides/ef-core#rows-saved-without-loading-them).
- **A value converter on a key, foreign key, or unique column must round-trip**, and Sluice
  can't check that it does: it never sees the stored value. So it refuses the model unless each
  such converter is one of EF's lossless built-ins or its class is marked `[SluiceRoundTrips]`,
  your promise. A converter from `HasConversion` lambdas moves into a class to be marked. See
  [Key folding](/guides/ef-core#key-folding).
- **Classes over one table are linked only when their key columns, and the schema's spelling,
  match.** Dependencies are named after tables, so a read model in another context over the same
  table is linked to the first class. One with other key columns, one over only one table of a
  TPT or TPC hierarchy, or one naming the schema differently (`null` against `"public"`) isn't:
  have its reads also record the other's `AllDependencies()` with `read.From`, or invalidate by
  hand. See [What a query records](/guides/ef-core#what-a-query-records).
- **Broad eviction, by design**: a plain query is evicted by any write to any type it reads, a
  `Children` list by a change to any row in it, and every read of a type by an `ExecuteUpdate`
  or `ExecuteDelete` on its table, unless it names its rows with `ForKey` or `ForKeys`. A keyed
  one still evicts every `Children` list of the type's foreign keys, and an update every reader
  by a unique column the statement names; past 100 keys, or when an update's SQL names the key
  column again (a `Where` through a navigation joins a table whose key is also `Id`), it evicts
  the whole type. See
  [What a query records](/guides/ef-core#what-a-query-records),
  [Bulk updates and deletes](/guides/ef-core#bulk-updates-and-deletes), and
  [Bulk writes by key](/guides/ef-core#bulk-writes-by-key).
- **A `DbTransaction` committed or rolled back outside EF is invalidated late**: when Sluice
  next looks, such as the flow's next cached read, the context's next command, its connection
  closing or, for a transaction EF began, its disposal, since EF raises no event for it. One you
  passed in with `UseTransaction` is checked before every cached read on that server, and every
  second, so other servers stop serving the old value at most a second after it ended. If you
  begin another transaction on that connection outside EF first, Npgsql hands back the same
  object, which then looks live: the first's writes publish when a context joins the next, or
  else when that one ends. A read in between can cache the old value. Commit through EF. See
  [Committing outside EF](/guides/transactions#committing-outside-ef).
- **A cached read of your own uncommitted writes is caught only where Sluice sees the
  transaction**: a `TransactionScope` by itself, and an EF or enlisted transaction once the
  flow writes in it after `SluiceFlow.Begin()`. Elsewhere the read returns the committed value.
  While such writes are pending, every cached read on the flow goes to the database. Writes
  rolled back to a savepoint made outside EF still count as pending. See
  [Reading your own uncommitted writes](/guides/strict-reads#reading-your-own-uncommitted-writes).
- **Relational providers only, and no `UseInternalServiceProvider`.** Computes take the context
  from a DI scope of their own, so it must be registered scoped or transient (every EF
  registration's default is scoped); a singleton is refused. See [Setup](/guides/ef-core#setup).
- **Tested on PostgreSQL only.** Key folding assumes PostgreSQL's comparison rules, and doesn't
  fold accent- or culture-sensitive collations. A date, duration, or time-of-day key, foreign key,
  or unique column on a provider other than PostgreSQL, SQL Server, or SQLite needs a column type
  with a size of 6 or more, such as `datetime(6)` or `time(6)`. See
  [Key folding](/guides/ef-core#key-folding).
- **Composite primary keys can't be read with `read.Entity`**, and `read.Children` takes a
  single-column foreign key. See [Keys](/guides/ef-core#keys).
- **Views, keyless types, functions, and SQL-query mappings** can't be read inside a compute
  except in a fused fetch. See [What's refused](/guides/ef-core#what-s-refused).

## What a compute can't read

- **Some reads go unchecked inside a compute**, such as `Find` of an entity a captured outside
  context already tracks, or Dapper on any connection. See
  [What isn't checked](/guides/strict-reads#what-isn-t-checked).
- **Some reads only work in a fused fetch**: raw SQL, views, keyless types, and your own
  database functions. See [The declared route](/guides/strict-reads#the-declared-route).
- **Query filters that read the context's state need the compute to set it**, from the query
  key, with `read.Db<AppDb>(db => db.TenantId = tenantId)`; Sluice can't check what the
  configure sets. Filters that read it where the configure can't set it (through a service the
  context holds, an unmarked property with a body, a method, a readonly field, or a virtual,
  init-only, or get-only property) are refused. See
  [Tenants](/guides/strict-reads#tenants-and-other-context-state).
- **`[SluiceContextState]` is partly taken on trust.** A filter may read a scoped tenant
  service through a context property marked with it, set by
  `read.Db<AppDb>((db, services) => …)`. Sluice checks that the property reads the same with and
  without the caller's async state, which catches a body reading the request, but a body that
  reads a static, a clock coarse enough to read the same twice, or a service it resolves later and that keeps the caller, caches
  one value for everyone with that key. See
  [A tenant read through a scoped service](/guides/strict-reads#a-tenant-read-through-a-scoped-service).
- **Invalidation isn't per tenant.** A write in one tenant evicts other tenants' cached reads
  of the same entity type, unless they read through `read.Children` on a tenant foreign key.
- **Entities that load lazily or hold their context can't be cached**, returned by a query or
  wrapped in a value of your own. Select the values you need. See
  [What throws inside a compute](/guides/strict-reads#what-throws-inside-a-compute).
- **Writes can't run inside a compute.** See [Writes](/guides/strict-reads#writes).
