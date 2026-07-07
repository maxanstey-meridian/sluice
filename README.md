# Sluice

Sluice is a .NET cache library that invalidates by observed dependencies, not broad tags.

A query records the resource addresses it actually read while computing. A write declares the resource addresses it
changed. Sluice evicts only cached entries whose observed reads intersect those changed addresses.

The runnable playground in `examples/playground` is the canonical example: a cached dashboard where Alice and Bob share
one dependency, while only Alice depends on an admin greeting.

## Status

Prototype / proof of concept. Targets `.NET 10` (`net10.0`). The core library has zero third-party dependencies.

## The domain stays the same

The example caches `dashboard:v1:{user}` for Alice and Bob:

- both dashboards read `user:{id}`
- both dashboards read shared `flag:dark_mode`
- only Alice reads `greeting:alice`

That gives the selective invalidation behavior:

- toggling `dark_mode` invalidates both cached dashboards
- changing Alice's greeting invalidates Alice only
- Bob remains a cache hit after the greeting changes

Sluice does not change your domain. Both modes use the same simple records:

```csharp
public sealed record UserId(string Value) : IResourceKey
{
    // ResourceKey is the stable string Sluice stores in resource addresses
    // and cache entry keys.
    public string ResourceKey => Value;

    public static implicit operator UserId(string value) => new(value);
}

public sealed record FeatureFlagId(string Value) : IResourceKey
{
    public static readonly FeatureFlagId DarkMode = new("dark_mode");

    public string ResourceKey => Value;

    public static implicit operator FeatureFlagId(string value) => new(value);
}

public sealed record User(UserId Id, string Name, string Role);

public sealed record FeatureFlag(FeatureFlagId Id, bool Enabled);

public sealed record Greeting(UserId Id, string Text);

public sealed record Dashboard(User User, FeatureFlag Flag, Greeting? Greeting);
```

---

## Generated mode

Generated mode is the default path: write a typed store interface, annotate the reads and writes, and keep the cached projection hand-written.

```csharp
[Sluice("GeneratedDashboard")]
public interface IGeneratedPlaygroundStore
{
    [ReadEntity("user")]
    public Task<User> GetUser(UserId id, CancellationToken ct);

    [ReadEntity("flag")]
    public Task<FeatureFlag> GetFlag(FeatureFlagId id, CancellationToken ct);

    [ReadEntity("greeting")]
    public Task<Greeting> GetGreeting(UserId id, CancellationToken ct);

    [WriteEntity("flag")]
    public Task ToggleFlag(FeatureFlagId id, CancellationToken ct);

    [WriteEntity("greeting")]
    public Task UpdateGreeting(UserId id, string text, CancellationToken ct);
}
```

That interface emits `GeneratedDashboardResources` and `GeneratedDashboardSluice`. Codegen replaces the repetitive resource wrapper, not the application query:

```csharp
// Generated mode keeps the same cached projection, but gets tracked reads and
// writes from the source-generated GeneratedDashboardSluice wrapper.
public sealed class DashboardCache
{
    private readonly SluiceKernel _sluice;
    private readonly PlaygroundStore _store;
    private readonly GeneratedDashboardSluice _dashboardSluice;
    private readonly CachedQuery<UserId, Dashboard> _dashboardQuery;

    public DashboardCache(SluiceKernel sluice, PlaygroundStore store)
    {
        _sluice = sluice;
        _store = store;
        _dashboardSluice = new GeneratedDashboardSluice(_sluice, _store);

        // The query is still hand-written. Codegen replaces the resource wrapper,
        // not the application projection.
        _dashboardQuery = new CachedQuery<UserId, Dashboard>(
            "dashboard",
            ComputeDashboard,
            ttl: TimeSpan.FromMinutes(5)
        );
    }

    private async ValueTask<Dashboard> ComputeDashboard(UserId id, IReadScope scope)
    {
        var user = await _dashboardSluice.User.Get(id, scope);
        var flag = await _dashboardSluice.Flag.Get(FeatureFlagId.DarkMode, scope);
        Greeting? greeting = null;

        if (user.Role == "admin")
        {
            greeting = await _dashboardSluice.Greeting.Get(id, scope);
        }

        return new Dashboard(user, flag, greeting);
    }

    public Task<Dashboard> GetDashboard(UserId user, CancellationToken ct) =>
        _sluice.Get(_dashboardQuery, user, ct);

    public async Task<FeatureFlag> ToggleDarkMode(CancellationToken ct)
    {
        await _dashboardSluice.ToggleFlag(FeatureFlagId.DarkMode, ct);
        return await _store.GetFlag(FeatureFlagId.DarkMode, ct);
    }

    public Task UpdateGreeting(UserId user, string text, CancellationToken ct) =>
        _dashboardSluice.UpdateGreeting(user, text, ct);

    public Task FlushAll(CancellationToken ct) => _sluice.FlushAllAsync(ct);
}
```

---

## Manual mode

Manual mode shows the same contract explicitly: resource definitions, tracked reads, tracked writes, and the cached projection all live in one class.

Sluice wraps reads and writes with typed resource definitions so invalidation follows the work your code already performs.

```csharp
// This is the hand-written Sluice layer for the playground.
// It names the resources, derives tracked reads/writes, and composes them into
// the cached dashboard projection that the UI exercises.
public sealed class DashboardCache
{
    private readonly SluiceKernel _sluice;
    private readonly PlaygroundStore _store;
    private readonly TrackedRead<UserId, User> _userRead;
    private readonly TrackedRead<FeatureFlagId, FeatureFlag> _flagRead;
    private readonly TrackedRead<UserId, Greeting> _greetingRead;
    private readonly CachedQuery<UserId, Dashboard> _dashboardQuery;
    private readonly TrackedWrite<FeatureFlagId> _flagWrite;
    private readonly TrackedWrite<UserId> _greetingWrite;

    public DashboardCache(SluiceKernel sluice, PlaygroundStore store)
    {
        _sluice = sluice;
        _store = store;

        // Resource definitions are pure identity: kind + name + key type.
        // The same resource name must be used by reads and writes so Sluice can
        // intersect "what was read" with "what changed" during invalidation.
        var userResource = new EntityResource<UserId>("user");
        var flagResource = new EntityResource<FeatureFlagId>("flag");
        var greetingResource = new EntityResource<UserId>("greeting");

        // .Read(storeMethod) pairs a resource address with the actual fetch.
        // Calling .Get(key, scope) inside the query body records the read
        // into the scope so Sluice can track which resources were observed.
        _userRead = userResource.Read((id, _) => _store.GetUser(id));
        _flagRead = flagResource.Read((id, _) => _store.GetFlag(id));
        _greetingRead = greetingResource.Read((id, _) => _store.GetGreeting(id));

        // Writes declare which resource address changed.
        // Sluice evicts cached entries whose recorded reads include that address.
        _flagWrite = flagResource.Write(_sluice);
        _greetingWrite = greetingResource.Write(_sluice);

        // The cached query is the projection users read from the playground.
        // The cache key becomes dashboard:v1:"alice" or dashboard:v1:"bob".
        _dashboardQuery = new CachedQuery<UserId, Dashboard>(
            "dashboard",
            ComputeDashboard,
            ttl: TimeSpan.FromMinutes(5)
        );
    }

    private async ValueTask<Dashboard> ComputeDashboard(UserId id, IReadScope scope)
    {
        // Both dashboards depend on their user row and the shared flag.
        var user = await _userRead.Get(id, scope);
        var flag = await _flagRead.Get(FeatureFlagId.DarkMode, scope);
        Greeting? greeting = null;

        // Alice is admin, so only Alice's dashboard observes greeting:alice.
        // Bob never reads it, so a greeting write should not evict Bob.
        if (user.Role == "admin")
        {
            greeting = await _greetingRead.Get(id, scope);
        }

        return new Dashboard(user, flag, greeting);
    }

    // Cache miss: compute the dashboard, record observed resource reads, store it.
    // Cache hit: return the stored Dashboard without calling PlaygroundStore.
    public Task<Dashboard> GetDashboard(UserId user, CancellationToken ct) =>
        _sluice.Get(_dashboardQuery, user, ct);

    public async Task<FeatureFlag> ToggleDarkMode(CancellationToken ct)
    {
        // dark_mode is shared, so both dashboard:v1:alice and dashboard:v1:bob
        // are affected after they have observed flag:dark_mode.
        await _flagWrite.Write(
            FeatureFlagId.DarkMode,
            _ => _store.ToggleFlag(FeatureFlagId.DarkMode),
            ct
        );

        return await _store.GetFlag(FeatureFlagId.DarkMode);
    }

    // greeting:{user} is selective. In the seeded data only Alice reads a greeting,
    // so updating Alice's greeting evicts Alice but leaves Bob cached.
    public Task UpdateGreeting(UserId user, string text, CancellationToken ct) =>
        _greetingWrite.Write(user, _ => _store.UpdateGreeting(user, text), ct);

    // Flush is the broad escape hatch: clear cache entries and dependency edges.
    public Task FlushAll(CancellationToken ct) => _sluice.FlushAllAsync(ct);
}
```

---

## Run the example

The playground runs both modes with fully isolated state (own store, kernel, event sink, and cache):

```bash
dotnet run --project examples/playground/Playground.csproj
```

Open `http://localhost:5300`. Tabs switch between Manual and Generated.

Key files:

- `examples/playground/Manual/` — hand-written mode
- `examples/playground/Generated/` — codegen mode
- `examples/playground/wwwroot/index.html` — tabbed visualization

`Program.cs` creates one runtime per mode:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5300");

var app = builder.Build();
app.UseStaticFiles();

// Each mode is a fully isolated app: own store, Sluice kernel, event sink,
// cached query, and materialized UI state.
var manual = new ManualPlaygroundRuntime();
var generated = new GeneratedPlaygroundRuntime();

app.MapManualEndpoints(manual);
app.MapGeneratedEndpoints(generated);

app.MapFallbackToFile("index.html");

Console.WriteLine("Sluice Playground running at http://localhost:5300");
await app.RunAsync();
```

---

## ResultKey (codegen)

Result-derived invalidation is easier in codegen. The write returns the created entity;
`ResultKey` derives its address from the return value.

```csharp
[WriteEntity("widget", ResultKey = nameof(Widget.Id))]
Task<Widget> CreateWidget(WidgetId id, WidgetInput input, CancellationToken ct);
```

The generator validates `ResultKey` at compile time — wrong property name, non-
`IResourceKey` type, or `Task` instead of `Task<T>` all produce `SLUICE001` diagnostics.

---

## Resource Addresses

A `ResourceAddress` is the unit of dependency tracking. It has three parts:

- `ResourceKind`: `Entity` or `Collection`
- `Name`: the resource name (e.g. `user`, `orders.byCustomer`)
- `Key`: the specific key (e.g. `alice`, `customer-123`)

```text
entity:user:alice
entity:flag:dark_mode
entity:greeting:alice
```

`EntityResource<TKey>.For(key)` and `CollectionResource<TKey>.For(key)` produce addresses.
`Wildcard()` produces an address that matches all keys of a resource (for bulk invalidation):

```csharp
var flagResource = new EntityResource<FeatureFlagId>("flag");

// Invalidate every cached entry that read flag:dark_mode.
await sluice.Invalidate(
    new WriteEffect(flagResource.For(FeatureFlagId.DarkMode)),
    ct);
```

---

## Escape Hatches

For one-off writes without a `TrackedWrite` field:

```csharp
var flagResource = new EntityResource<FeatureFlagId>("flag");

// Run the work, then invalidate the declared addresses.
await sluice.Apply(
    ct => store.ToggleFlag(FeatureFlagId.DarkMode),
    new WriteEffect(flagResource.For(FeatureFlagId.DarkMode)),
    ct);

// Or if the work already committed — just invalidate.
await sluice.Invalidate(
    new WriteEffect(flagResource.For(FeatureFlagId.DarkMode)),
    ct);
```

For result-derived addresses (e.g. a database-generated ID):

```csharp
var order = await sluice.Apply(
    ct => store.CreateOrder(customerId, input, ct),
    new WriteEffect<Order>(OrderResources.OrdersByCustomer.For(customerId))
        .ChangesResult(order => OrderResources.Order.For(order.Id)),
    ct);
```

For custom tracking inside a compute body (computed addresses, conditional reads):

```csharp
var query = new CachedQuery<FeatureFlagId, FeatureFlag>("flag.byId",
    async (id, scope) =>
    {
        // Manual tracking instead of TrackedRead.
        return await scope.Track(
            new ResourceAddress(ResourceKind.Entity, "flag", id.ResourceKey),
            ct => store.GetFlag(id));
    });
```

---

## Observability

```csharp
// Text snapshot of the runtime dependency graph.
var graph = await sluice.DumpGraphAsync(ct);

// Static metadata for registered operations.
var manifest = sluice.Describe();
```

Example `DumpGraphAsync` output:

```text
OPERATIONS:
  dashboard:v1:"alice"
    reads:
      entity:user:alice
      entity:flag:dark_mode
      entity:greeting:alice
    cached: 2026-07-06T10:20:01+00:00

  dashboard:v1:"bob"
    reads:
      entity:user:bob
      entity:flag:dark_mode
    cached: 2026-07-06T10:20:02+00:00

RESOURCE ADDRESSES:
  entity:flag:dark_mode
    invalidates:
      dashboard:v1:"alice"
      dashboard:v1:"bob"

  entity:greeting:alice
    invalidates:
      dashboard:v1:"alice"
```

Each cached entry lists the resources it tracked. Each resource address lists the entries
it would invalidate.

---

---

## Error Handling During Compute

If the compute body throws, the exception propagates directly to the caller. Nothing is cached — no partial or stale value is stored.

The stampede lease is always released (`try/finally`), so a failed leader never blocks subsequent callers:

1. **Leader throws** → lease released → entry not stored → exception propagates to the leader's caller.
2. **Followers** poll the cache for `WaitTimeout` (default 5s). The entry never appears (the leader stored nothing).
3. After timeout, a follower tries to acquire the lease. Since the leader released it, the follower succeeds and becomes the new leader — it computes itself.
4. If the lease is still held (another follower grabbed it first), the follower emits `stampede.timeout` and computes without the lease.

If the underlying store consistently throws, every concurrent caller eventually receives the exception. Each follower incurs one `WaitTimeout` delay before its own attempt, so a persistently failing compute body adds up to `WaitTimeout` latency to concurrent requests.

Tune `StampedeOptions` to control this:

```csharp
var sluice = new SluiceKernel(
    new InMemoryCacheStore(),
    stampedeOptions: new StampedeOptions
    {
        WaitTimeout = TimeSpan.FromSeconds(2),  // follower poll deadline
        LeaseTtl = TimeSpan.FromSeconds(30),    // max leader compute time
        MaxBackoff = TimeSpan.FromMilliseconds(50),
    });
```

---

## Codegen Attributes

| Attribute                                   | Emits                                                            |
|---------------------------------------------|------------------------------------------------------------------|
| `[Sluice]` on interface                     | `{Name}Resources` + `{Name}Sluice` classes                       |
| `[ReadEntity("name")]`                      | `EntityResource<TKey>` field + `TrackedRead`                     |
| `[ReadCollection("name", "byKey")]`         | `CollectionResource<TKey>` field + `TrackedRead`                 |
| `[WriteEntity("name")]`                     | Address in generated `TrackedWrite`                              |
| `[WriteCollection("name", "byKey")]`        | Address in generated `TrackedWrite`                              |
| `ResultKey = nameof(X.Id)` (on write attrs) | Result-derived address resolver in `TrackedWrite<TKey, TResult>` |

Resources are canonicalised by name. `[ReadEntity("user")]` and `[WriteEntity("user")]`
produce a single `User` field — not duplicates.

Generated naming: strip the `I` prefix from the interface, append `Sluice`.
`IUserStore` → `UserStoreSluice`. Override with `[Sluice("User")]` → `UserSluice`.
A custom name ending in `Sluice` (e.g. `[Sluice("UserSluice")]`) is treated as invalid
base-name input and emits `SLUICE002` - no source is generated.

## Build And Test

```bash
dotnet build Sluice.sln
dotnet test Sluice.sln
```

## Redis Backing

Sluice ships two Redis-backed stores in the separate `Sluice.Redis` package:

- `RedisCacheStore` — replaces `InMemoryCacheStore` for distributed caching. Serializes `CacheEntry<TValue>` as JSON
  using `System.Text.Json` with configurable `JsonSerializerOptions`.
- `RedisGraphStore` — replaces `InMemoryGraphStore` for distributed invalidation. Stores resource addresses as Redis SET
  members via `SADD`/`SMEMBERS`/`SREM`.

Primary wiring uses `SluiceRedis.Create`, which automatically wires all three Redis components with a shared circuit
breaker for resilience:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("redis://localhost:6379");
var sluice = SluiceRedis.Create(redis);
```

`SluiceRedis.Create` wraps `RedisCacheStore`, `RedisGraphStore`, and `RedisStampedeCoordinator` in resilient decorators
that share one circuit breaker. When Redis is unavailable, cache reads return misses, cache writes are silently dropped,
and no Redis exceptions propagate to the caller. The circuit opens after 5 consecutive failures and recovers after a
10-second cooldown with a single probe call.

For custom configurations (different key prefix, serializer options, stampede options, or circuit breaker settings),
construct the stores manually:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("redis://localhost:6379");
var cacheStore = new RedisCacheStore(redis, "sluice");
var graphStore = new RedisGraphStore(redis, "sluice");
var stampede = new RedisStampedeCoordinator(redis);
var sluice = new SluiceKernel(cacheStore, graphStore, stampedeCoordinator: stampede);
```

Both `RedisCacheStore` and `RedisGraphStore` must be passed together to `SluiceKernel` for distributed deployments.
Passing only `RedisCacheStore` produces distributed cache + in-process graph — a hybrid that causes invalidation misses
across processes.

Stampede protection coalesces redundant recompute across processes. When multiple processes miss the same cache entry
simultaneously, only one computes while others poll the cache with backoff until the entry appears. The
`RedisStampedeCoordinator` uses `SET NX PX` for lease acquisition and an atomic Lua compare-and-delete for release. The
Lua constraint (no Lua scripts in Redis backing) is relaxed specifically for this release script — `RedisCacheStore` and
`RedisGraphStore` remain Lua-free. Lease TTL (default 30s) bounds how long one process may own a recompute; it is
separate from cache entry TTL, which may be minutes or hours.

Distributed epoch fencing prevents cross-process invalidation races via the `RedisEpochFence`. The fence uses a
Redis `INCR`-based global epoch counter (`{keyPrefix}:epoch`) and a bounded Redis sorted set (`{keyPrefix}:inval`) that
records each invalidation with its epoch. When a compute finishes, the fence re-reads the global epoch and scans the
sorted set for overlapping invalidations that occurred during the compute. If any are found, the just-written entry is
self-invalidated. The sorted set is trimmed by epoch count (not TTL) so that long-running computes conservatively
invalidate rather than miss an expired record. `SluiceRedis.Create` wires `RedisEpochFence` automatically alongside
the cache, graph, and stampede coordinator. For single-process deployments, `InMemoryEpochFence` provides the same
fencing without Redis.

The caller owns the `ConnectionMultiplexer` and its disposal lifecycle. Sluice does not dispose it.

Serialization uses `System.Text.Json` with default options. Pass `JsonSerializerOptions` to the `RedisCacheStore`
constructor for camelCase property naming, string-valued enums, or other customization:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};
var cacheStore = new RedisCacheStore(redis, "sluice", options);
```

Topology: single-node or managed endpoint (e.g. Azure Cache for Redis non-cluster mode, AWS ElastiCache non-cluster
mode). Primary/replica managed endpoints are acceptable — `StackExchange.Redis` routes writes such as `KeyDeleteAsync`
to the primary. Redis Cluster is not supported — `SCAN`-based clear and flush assume single-node key distribution.

Consistency is eventual. TTL is the backstop for distributed recompute and invalidation races.

For multi-tenant isolation on a shared Redis, use matching `keyPrefix` parameters (default `"sluice"`) on both
`RedisCacheStore` and `RedisGraphStore`.

## API Surface

**App-facing:**

- `SluiceKernel`, `ISluice`
- `CachedQuery<TKey, TValue>` — cached operation definition
- `TrackedRead<TKey, TValue>` — resource address + store delegate
- `TrackedWrite<TKey>`, `TrackedWrite<TKey, TResult>` — write + invalidation
- `IReadScope` — dependency tracking context inside a compute body
- `EntityResource<TKey>`, `CollectionResource<TKey>` — resource definitions
- `ResourceAddress`, `IResourceKey`, `ResourceKind`
- `WriteEffect`, `WriteEffect<T>` — escape hatch
- `[Sluice]`, `[ReadEntity]`, `[ReadCollection]`, `[WriteEntity]`, `[WriteCollection]`

**Kernel (tested directly, lower-level):**

- `CachedOperation<TKey, TValue>`, `OperationRegistry`, `OperationContext`
- `ICacheStore`, `InMemoryCacheStore`
- `IGraphStore`, `InMemoryGraphStore`

**Redis backing (separate package):**

- `RedisCacheStore`, `RedisGraphStore`, `RedisStampedeCoordinator` (in `src/Sluice.Redis/`)
- `IEpochFence`, `InMemoryEpochFence` (in `src/Sluice/`)
- `RedisEpochFence` (in `src/Sluice.Redis/`)

## Limitations

- Invalidation only knows about reads through `TrackedRead.Get` or `scope.Track`.
- Writes must declare every resource address they changed. Missing addresses leave stale entries.
- If a write succeeds and the process crashes before invalidation, stale entries remain until TTL or flush.
- In-memory cache/graph stores are for tests and single-process apps.
- Redis backing is single-node or managed endpoint only. Redis Cluster topology is not supported.
- The dependency graph is process-local. Cross-process invalidation requires Redis backing.
- Benchmark overhead is dominated by fixed tracking cost for tiny in-memory operations; Sluice is intended for cached
  operations expensive enough to justify dependency tracking.
