# Configuration

Sluice has no options of its own. `AddSluice` hands you FusionCache's builder, and everything
you configure there is FusionCache's, documented by
[FusionCache](https://github.com/ZiggyCreatures/FusionCache/tree/main/docs): durations, the
distributed cache (a shared second level, usually Redis), the backplane (messages that keep
each server's memory cache in sync), the serialiser, fail-safe (serving the last good value
when a compute fails), timeouts and auto-clone.

```csharp
using Sluice;
using ZiggyCreatures.Caching.Fusion;

builder.Services.AddSluice(cache =>
    cache.WithDefaultEntryOptions(options => options.Duration = TimeSpan.FromMinutes(5))
);
```

The builder's methods are FusionCache's extension methods, so they need
`using ZiggyCreatures.Caching.Fusion;`. Call `AddSluice` once, with all of this; a second call
throws.

## What Sluice sets

Sluice runs on a FusionCache of its own, named `sluice`. It applies these after your
configuration, overriding anything that conflicts:

| Setting | Value | Why |
|---|---|---|
| key prefix | the cache name | Sluice's keys, tags and clears can't touch another FusionCache on the same Redis |
| `DisableTagging` | `false` | invalidation is tagging |
| `RemoveByTagBehavior` | `Remove` | under `Expire`, fail-safe would serve a value that was just invalidated |
| `FactorySoftTimeout`, `FactoryHardTimeout` | infinite, per call | a timed-out compute would keep running, and using what it holds, after the caller moved on |
| `AllowTimedOutFactoryBackgroundCompletion`, `EagerRefreshThreshold` | off, per call | both store a value marked as built when its compute *finished*, so an invalidation that landed while it ran could be missed |
| `ClearAsync` | without fail-safe | a cleared value is never served as a fallback |
| `TagsDefaultEntryOptions.Priority`, `.Size` | `NeverRemove`, `0` | under a memory size limit, a tag marker evicted or refused for want of room would let an invalidated value be served again |

And it refuses, when `SluiceCache` is first resolved, two `InternalStrings` settings that would
let entries and tags collide. Both throw `InvalidOperationException`; leave `InternalStrings`
at its defaults, or call `SetToSafeStrings()` with its default special character, `_`:

- a `TagCacheKeyPrefix` that doesn't start with `__fc`, set directly or through
  `SetToSafeStrings` with another character. FusionCache keeps a tag's marker at the prefix plus
  the tag, among the entries, and treats any key under the prefix as tag data, so under the
  prefix `t:`, query `t`'s entry for key `x` would be tag `x`'s marker, and never invalidated;
- a `ClearRemoveTag` or `ClearExpireTag` with a `:` that has a name before it. `a:b` is also
  the tag of source `a`'s key `b`, so invalidating that key would clear the cache on every node.

Every invalidation and clear is also published a second time, 5 s after the first, timed by the
`TimeProvider` registered in DI (`TimeProvider.System` unless you register one). FusionCache 2.9
can lose an invalidation that lands while the same server is checking that tag
(FusionCache#659); the second publish restores it. A value cached in those 5 s is evicted again,
so a hot entry is computed once more per invalidation. Disposing the service provider publishes
whatever is still waiting at once, and waits for it.

So FusionCache's factory timeouts (a factory is FusionCache's word for a compute) don't apply to
Sluice. A slow data source makes the caller wait; put the timeout on the data source instead (a
command timeout, an HTTP timeout).

## Durations

An entry lives for its query's `Duration`, or the cache's `DefaultEntryOptions.Duration`, which
is FusionCache's 30 seconds unless you set it. If an invalidation is ever missed, the duration
is how long the old value can live. Keep it short for data something outside your app writes:
those writes evict nothing unless they
[invalidate by hand](/guides/ef-core#writes-sluice-can-t-see).

Sluice passes its own entry options on every call, built from `DefaultEntryOptions`, so a
FusionCache `DefaultEntryOptionsProvider` (options picked per key) doesn't apply. Use a query's
`Duration` instead.

## Limiting memory

Sluice's memory cache has no size limit unless you give it one. To cap it, pass FusionCache a
memory cache with a `SizeLimit`, and give every entry a size, either by default or per query
([`Query.Size`](/guides/sources-and-queries#size)):

```csharp
using Microsoft.Extensions.Caching.Memory;

builder.Services.AddSluice(cache =>
    cache
        .WithMemoryCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 }))
        .WithDefaultEntryOptions(options => options.Size = 1)
);
```

With a limit set, the memory cache refuses an entry with no size by throwing, so a query left
without one fails on its first store. When the cache is full, new entries aren't stored and
older ones are compacted away; either way a later call just computes again.

Tag markers, the small entries that record when each dependency was last invalidated, are
exempt: Sluice stores them with size 0 and `NeverRemove`, whatever you set, because a marker
that went missing would read as "never invalidated" and an evicted value would come back. They
aren't counted against the limit, so the cache can hold more than the limit by one small entry
per dependency it has seen, until each expires: by default up to ten days after it was last
written (`TagsDefaultEntryOptions.FailSafeMaxDuration`; see [the lifetime rule](#the-lifetime-rule)).

## Logging

The EF package needs logging registered: a failure to invalidate after a commit is logged, not
thrown. ASP.NET Core and the Generic Host register it. Elsewhere, call
`services.AddLogging()`.

The EF package also logs, at `Information`, the first time each cached query reads whole types
(see [What a query records](/guides/ef-core#what-a-query-records)): "Sluice's cached query
{Query} depends on every row of {EntityTypes}, so a write to any of their rows evicts it. …".
It's event id 2, under the category `Sluice.EntityFrameworkCore.TrackedReads`, and it goes
through EF's logger factory, which under dependency injection is your app's. To hide it, raise
that category to `Warning`.

It also logs once per entity type and cache when a save of that type has to evict `read.Children` lists
under every parent, not just the parent the row had (see
[Row versions](/guides/ef-core#row-versions)): event id 3 when the type has no row version, 4
when it's stored across several tables. Those are under the category
`Sluice.EntityFrameworkCore.SluiceInterceptor`, so raise that one to hide them.

Two more, under the same category, mean something to fix:

- event id 5, at `Warning`, once per cache: a save or bulk write inside a `TransactionScope` created without
  `TransactionScopeAsyncFlowOption.Enabled` couldn't see the scope where it completed, so Sluice
  invalidated at once and will again when the scope completes. See
  [TransactionScope with async code](/guides/transactions#transactionscope-with-async-code).
- event id 6, at `Error`: a save reported rows written but ran no command Sluice saw, so nothing
  was invalidated for it. Sluice reads what a save writes at its first command, so a provider
  or interceptor that writes without one isn't supported.

FusionCache logs several lines at `Information` for every cache call. To keep them out of your
logs, raise its category in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "ZiggyCreatures": "Warning"
    }
  }
}
```

## Multi-node

On several servers, add FusionCache's distributed cache and backplane, here both on Redis. An
invalidation on one server then reaches the others:

```csharp
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Sluice;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

// Give up on a Redis call after one second, not StackExchange.Redis's default of five.
var redis = "localhost:6379,asyncTimeout=1000,syncTimeout=1000";

builder.Services.AddSluice(cache =>
    cache
        .WithDefaultEntryOptions(options => options.Duration = TimeSpan.FromMinutes(5))
        .WithSerializer(new FusionCacheSystemTextJsonSerializer())
        .WithDistributedCache(new RedisCache(new RedisCacheOptions { Configuration = redis }))
        .WithBackplane(new RedisBackplane(new RedisBackplaneOptions { Configuration = redis }))
        .WithOptions(options =>
        {
            // After a failure, skip Redis for five seconds rather than wait on it every call.
            options.DistributedCacheCircuitBreakerDuration = TimeSpan.FromSeconds(5);
            options.BackplaneCircuitBreakerDuration = TimeSpan.FromSeconds(5);
        })
);
```

The packages are `ZiggyCreatures.FusionCache.Serialization.SystemTextJson`,
`ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis` and
`Microsoft.Extensions.Caching.StackExchangeRedis`. The timeouts and both circuit breakers are
the recommended settings; the next section says why.

What multi-node can't guarantee is in [Limitations](/misc/limitations#multi-node).

### When Redis is slow or down

Sluice invalidates through FusionCache tags: each cached value is labelled with what it read,
and invalidating writes a timestamp for a label. Each dependency you invalidate writes one or
two tags to Redis, and announces each over the backplane. `InvalidateAsync`, and every EF save,
waits for those writes. FusionCache puts no timeout on them, so only the Redis client's own
timeout stops them waiting.

- **A slow Redis** holds up each invalidation, and each EF save, until the client times out.
  That's why the sample sets one second.
- **A failed write doesn't throw** out of `InvalidateAsync` or `SaveChanges`. FusionCache keeps
  the tag and writes it again once Redis is back (its auto-recovery, on by default). Until
  then, other servers don't hear of the invalidation. If the server restarts first, the retry
  is lost, and the old value lives until its entry expires.
- **The circuit breakers** stop every call waiting on a Redis that has just failed: for the
  breaker's duration, FusionCache skips Redis and leaves the write to auto-recovery.

Don't use `TagsDefaultEntryOptions.DistributedCacheHardTimeout` for this. It only limits
reads, so it doesn't shorten an invalidation. And when it cuts off a read of a tag, the server
treats that tag as never invalidated for up to an hour.

### Fail fast at startup

The lifetime rule below, and the `InternalStrings` checks in
[What Sluice sets](#what-sluice-sets), run when `SluiceCache` is first resolved. To find a bad
configuration at deploy time rather than on the first request, resolve it once at startup:

```csharp
var app = builder.Build();
app.Services.GetRequiredService<SluiceCache>();
```

## The lifetime rule

FusionCache invalidates by tag: it records when each tag was last invalidated (Sluice calls
this *tag data*), and an entry stored before that is treated as gone. Tag data is cached too,
and expires. If it expires before the entry does, the entry looks valid again: a newly started
server serves an invalidated value from Redis, or, without Redis, the same server serves it
from memory.

So in memory and in Redis alike, an entry must not outlive the tag data:

- in memory: the entry's `MemoryCacheDuration ?? Duration` (or `FailSafeMaxDuration` when
  fail-safe is on and that's longer), plus `JitterMaxDuration`, must be at most the same figure
  for `TagsDefaultEntryOptions`;
- in the distributed cache: `DistributedCacheDuration ?? Duration` (or
  `DistributedCacheFailSafeMaxDuration ?? FailSafeMaxDuration` with fail-safe on) must be at most
  the same figure for `TagsDefaultEntryOptions`.

FusionCache turns fail-safe on for tag data, which keeps it for up to ten days in memory and in
Redis, so the defaults pass easily. A breach throws `InvalidOperationException` when
`SluiceCache` is first resolved (with EF, that's when the first context is configured), or, for
a query with its own `Duration`, the first time the query is used. The usual ways to trip it are a long fail-safe on
your entries, or turning fail-safe off for the tag data:

```text
The Sluice cache's DefaultEntryOptions keeps entries in memory for up to 30.00:00:00, longer
than Sluice's tag data there (10.00:00:00), so an invalidated entry could come back once its tag
data expires. Shorten Duration, MemoryCacheDuration, FailSafeMaxDuration or JitterMaxDuration, or
lengthen TagsDefaultEntryOptions.MemoryCacheDuration or TagsDefaultEntryOptions.FailSafeMaxDuration.
```

Lengthen the tag data to match:

```csharp
builder.Services.AddSluice(cache =>
    cache
        .WithDefaultEntryOptions(options =>
        {
            options.Duration = TimeSpan.FromMinutes(5);
            options.IsFailSafeEnabled = true;
            options.FailSafeMaxDuration = TimeSpan.FromDays(30);
        })
        .WithOptions(options =>
            options.TagsDefaultEntryOptions.FailSafeMaxDuration = TimeSpan.FromDays(30)
        )
);
```

An entry's lifetime counts from when it's stored, and tag data's from the invalidation. So with
exactly equal lifetimes, a gap as long as one compute remains. With the defaults' ten days,
that never matters.

## Shared instances and auto-clone

From memory, FusionCache hands every caller the same object, EF entities included. Modifying a
cached value changes it for every later caller, and no invalidation ever undoes that. Treat
cached values as immutable (records with `init` properties make that the default), or turn on
FusionCache's auto-clone, which hands each caller its own copy. It copies through the
serialiser, so it needs one, and every cached value must survive a round trip through it:

```csharp
using Sluice;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

builder.Services.AddSluice(cache =>
    cache
        .WithSerializer(new FusionCacheSystemTextJsonSerializer())
        .WithDefaultEntryOptions(options => options.EnableAutoClone = true)
);
```
