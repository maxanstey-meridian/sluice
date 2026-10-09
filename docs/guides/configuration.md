# Configuration

Sluice has no options of its own. `AddSluice` takes FusionCache's builder, and everything you
configure there (durations, the distributed cache, the backplane, the serialiser, fail-safe,
timeouts, auto-clone) is FusionCache's, documented by
[FusionCache](https://github.com/ZiggyCreatures/FusionCache/tree/main/docs).

```csharp
builder.Services.AddSluice(cache =>
    cache.WithDefaultEntryOptions(options => options.Duration = TimeSpan.FromMinutes(5))
);
```

## What Sluice sets

Sluice runs on a FusionCache of its own, named `sluice`. It applies these after your
configuration, overriding anything that conflicts:

| Setting | Value | Why |
|---|---|---|
| key prefix | the cache name | Sluice's keys, tags and clears can't touch another FusionCache on the same Redis |
| `DisableTagging` | `false` | invalidation is tagging |
| `RemoveByTagBehavior` | `Remove` | under `Expire`, fail-safe would serve a value that was just invalidated |
| `FactorySoftTimeout`, `FactoryHardTimeout` | infinite, per call | a timed-out compute would keep using the caller's `DbContext` after the caller moved on |
| `AllowTimedOutFactoryBackgroundCompletion`, `EagerRefreshThreshold` | off, per call | both store a value stamped when its compute *finished*, which can ignore an invalidation that landed mid-compute |
| `ClearAsync` | without fail-safe | a cleared value is never served as a fallback |

So FusionCache's factory timeouts don't apply to Sluice computes. A slow data source makes the
caller wait; bound it in the data source (a command timeout, an HTTP timeout).

## Durations

An entry lives for its query's `Duration`, or the cache's `DefaultEntryOptions.Duration`, which
is FusionCache's 30 seconds unless you set it. The duration is the backstop for any missed
invalidation. Keep it short for data something outside your app writes: those writes evict
nothing unless they [invalidate by hand](/guides/ef-core#writes-sluice-can-t-see).

Sluice passes its own entry options on every call, built from `DefaultEntryOptions`. A
FusionCache `DefaultEntryOptionsProvider`, which picks options per key, doesn't apply to Sluice
calls. Use a query's `Duration` instead.

## Logging

The EF package needs logging registered: a failure to invalidate after a commit is logged, not
thrown. ASP.NET Core and the Generic Host register it. Elsewhere, call
`services.AddLogging()`.

## Multi-node

On several nodes, add FusionCache's distributed cache and backplane. An invalidation on one
node then reaches the others:

```csharp
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

Each dependency you invalidate writes one or two tags to Redis, and announces each over the
backplane. `InvalidateAsync`, and every EF save, waits for those writes. FusionCache puts no
timeout on them, so the Redis client's own timeout is what bounds them.

- **A slow Redis** holds up each invalidation, and each EF save, until the client times out.
  That's why the sample sets one second.
- **A failed write doesn't throw** out of `InvalidateAsync` or `SaveChanges`. FusionCache keeps
  the tag and writes it again once Redis is back (its auto-recovery, on by default). Until
  then, other nodes don't hear of the invalidation. If the node restarts first, the retry is
  lost, and the entries' duration bounds the stale value.
- **The circuit breakers** stop every call waiting on a Redis that has just failed: for their
  duration, FusionCache skips Redis and leaves the write to auto-recovery.

Don't use `TagsDefaultEntryOptions.DistributedCacheHardTimeout` for this. It only bounds
reads, so it doesn't shorten an invalidation. And when it cuts off a read of tag data, the node
treats that tag as never invalidated for up to an hour.

### Fail fast at startup

The lifetime rule below is checked when `SluiceCache` is first resolved. To find a bad
configuration at deploy time rather than on the first request, resolve it once at startup:

```csharp
var app = builder.Build();
app.Services.GetRequiredService<SluiceCache>();
```

## The lifetime rule

FusionCache invalidates by tag: it records when each tag was invalidated (Sluice calls this tag
data), and an entry older than its tag's latest invalidation is treated as gone. If the tag data
expires before the entry, the entry looks valid again: a fresh node serves an invalidated value
from Redis, or, without Redis, the same node serves it from memory.

So at each level, an entry must not outlive the tag data:

- in memory: the entry's `MemoryCacheDuration ?? Duration` (or `FailSafeMaxDuration` when
  fail-safe is on and that's longer), plus `JitterMaxDuration`, must be at most the same figure
  for `TagsDefaultEntryOptions`;
- in the distributed cache: `DistributedCacheDuration ?? Duration` (or
  `DistributedCacheFailSafeMaxDuration ?? FailSafeMaxDuration` with fail-safe on) must be at most
  the same figure for `TagsDefaultEntryOptions`.

FusionCache keeps tag data with fail-safe on, for up to ten days at each level, so the defaults
pass easily. A breach throws `InvalidOperationException` when `SluiceCache` is first resolved
(with EF, that's when the first context is configured), or, for a query with its own
`Duration`, the first time the query is used. The usual ways to trip it are a long fail-safe on
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

Entry lifetimes count from when an entry is stored, and tag data's from the invalidation, so at
exactly equal lifetimes a gap of up to one compute's duration remains. Under the defaults that's
moot.

## Shared instances and auto-clone

From memory, FusionCache hands every caller the same instance, and that includes entities from
the EF read helpers. Changing a cached value changes it for every later caller, and nothing
evicts that edit. Treat cached values as immutable (records with `init` properties make that
the default), or turn on FusionCache's auto-clone, which hands each caller its own copy. It
copies through the serialiser, so it needs one, and every cached value must survive a round
trip through it:

```csharp
builder.Services.AddSluice(cache =>
    cache
        .WithSerializer(new FusionCacheSystemTextJsonSerializer())
        .WithDefaultEntryOptions(options => options.EnableAutoClone = true)
);
```
