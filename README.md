# Sluice

Sluice is a .NET cache library that invalidates by observed dependencies, not broad tags.

A query records the resource addresses it actually read while computing. A write declares the resource addresses it changed. Sluice evicts only cached entries whose observed reads intersect those changed addresses.

**Three tiers, all fully functional:**

1. **Hand-written** — resource definitions + `TrackedRead` + `TrackedWrite` + `CachedQuery`. ~30 lines.
2. **Codegen** — annotate your store interface with attributes. The generator writes Tier 1 for you.
3. **Escape hatch** — `sluice.Apply(work, effect, ct)` or `sluice.Invalidate(effect, ct)` for one-off writes.

Queries always stay hand-written — projection composition is application logic.

## Status

Prototype / proof of concept. Targets `.NET 10` (`net10.0`). The core library has zero third-party dependencies.

## The domain

These types are identical across all tiers. Sluice does not change your domain.

```csharp
// Keys implement IResourceKey. ResourceKey is used in resource addresses —
// it must be stable, culture-invariant, and unique within the resource.
public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record User(UserId Id, string Name, string Email);
public sealed record UserSettings(UserId Id, bool DarkMode, string Language);
public sealed record UserPreferences(UserId Id, string Theme);
public sealed record UserProfile(
    UserId Id, string Name, string Email,
    bool DarkMode, string Language, string Theme);
public sealed record UpdateUserInput(string Name, bool DarkMode, string Theme);

// Your store. Nothing changes here in any tier.
public interface IUserStore
{
    Task<User> GetUser(UserId id, CancellationToken ct);
    Task<UserSettings> GetSettings(UserId id, CancellationToken ct);
    Task<UserPreferences> GetPreferences(UserId id, CancellationToken ct);
    Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct);
}
```

---

## Tier 0 — No Sluice

Every call hits the store. No cache, no dependency tracking.

```csharp
public sealed class UserService(IUserStore store)
{
    public async Task<UserProfile> GetProfile(UserId id, CancellationToken ct)
    {
        var user = await store.GetUser(id, ct);
        var settings = await store.GetSettings(id, ct);
        var preferences = await store.GetPreferences(id, ct);

        return new UserProfile(
            id, user.Name, user.Email,
            settings.DarkMode, settings.Language, preferences.Theme);
    }

    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct) =>
        store.UpdateUser(id, input, ct);
}
```

```csharp
var svc = new UserService(store);

await svc.GetProfile(new UserId("alice"), ct);  // 3 store calls
await svc.GetProfile(new UserId("alice"), ct);  // 3 more — no cache
```

---

## Tier 1 — Hand-written Sluice

### Resource definitions

Pure data. No store, no sluice. Calling `.For(id)` produces a concrete `ResourceAddress`.

```csharp
// Static resource definitions — kind + name + key type.
// These are the identity of the data you cache around.
// Reads, writes, and wildcards all reference them by name.
public static class UserResources
{
    public static readonly EntityResource<UserId> User = new("user");
    public static readonly EntityResource<UserId> Settings = new("userSettings");
    public static readonly EntityResource<UserId> Preferences = new("userPreferences");
}
```

### Sluice wrapper

Primary constructor. Field initializers make the derivation from resource definition
visible at each declaration — `UserResources.User.Read(store.GetUser)` is right there.

```csharp
// ISluice captured for writes. IUserStore captured for data access.
// Each TrackedRead is derived from its resource definition via .Read().
public sealed class UserSluice(ISluice sluice, IUserStore store)
{
    // .Read(storeMethod) pairs a resource definition with its store delegate.
    // .Get(key, scope) records the dependency and fetches the value.
    public readonly TrackedRead<UserId, User> User =
        UserResources.User.Read(store.GetUser);

    public readonly TrackedRead<UserId, UserSettings> Settings =
        UserResources.Settings.Read(store.GetSettings);

    public readonly TrackedRead<UserId, UserPreferences> Preferences =
        UserResources.Preferences.Read(store.GetPreferences);

    // TrackedWrite captures ISluice + address delegates.
    // .Write(key, work, ct) runs the work, then invalidates affected entries.
    public readonly TrackedWrite<UserId> UpdateUser = new(
        sluice,
        UserResources.User.For,
        UserResources.Settings.For,
        UserResources.Preferences.For);
}
```

Queries can't be field initializers on `UserSluice` (CS0236: they reference sibling
reads). They live in a separate class that captures the `UserSluice` instance:

```csharp
// Queries compose tracked reads into cached projections.
// The compute body is application logic — always hand-written.
public sealed class UserQueries(UserSluice users)
{
    public readonly CachedQuery<UserId, UserProfile> Profile =
        new("user.profile", id => id.Value, async (id, scope) =>
        {
            var u = await users.User.Get(id, scope);
            var s = await users.Settings.Get(id, scope);
            var p = await users.Preferences.Get(id, scope);
            return new UserProfile(
                id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
        });
}
```

### Call site

```csharp
var sluice = new SluiceKernel(new InMemoryCacheStore());
var store  = new InMemoryUserStore();
var users  = new UserSluice(sluice, store);
var queries = new UserQueries(users);

// Cache miss: 3 store calls, tracks 3 dependencies, caches result.
await sluice.Get(queries.Profile, new UserId("alice"), ct);

// Cache hit: 0 store calls.
await sluice.Get(queries.Profile, new UserId("alice"), ct);

// Write: runs work delegate, invalidates user/settings/preferences for alice.
await users.UpdateUser.Write(
    new UserId("alice"),
    ct => store.UpdateUser(new UserId("alice"), new("Alice 2", true, "dark"), ct),
    ct);

// Cache miss: 3 store calls, fresh result.
await sluice.Get(queries.Profile, new UserId("alice"), ct);
```

---

## Tier 2 — Codegen

Annotate your store interface. The generator writes the resource class and sluice wrapper
for you.

### What you write

```csharp
[Sluice]
public interface IUserStore
{
    [ReadEntity("user")]
    Task<User> GetUser(UserId id, CancellationToken ct);

    [ReadEntity("userSettings")]
    Task<UserSettings> GetSettings(UserId id, CancellationToken ct);

    [ReadEntity("userPreferences")]
    Task<UserPreferences> GetPreferences(UserId id, CancellationToken ct);

    [WriteEntity("user")]
    [WriteEntity("userSettings")]
    [WriteEntity("userPreferences")]
    Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct);
}
```

### What the generator emits

```csharp
// UserStoreResources.g.cs — same as hand-written UserResources.
public static class UserStoreResources
{
    public static readonly EntityResource<UserId> User = new("user");
    public static readonly EntityResource<UserId> Settings = new("userSettings");
    public static readonly EntityResource<UserId> Preferences = new("userPreferences");
}

// UserStoreSluice.g.cs — same as hand-written UserSluice.
public sealed class UserStoreSluice(ISluice sluice, IUserStore store)
{
    public readonly TrackedRead<UserId, User> User =
        UserStoreResources.User.Read(store.GetUser);

    public readonly TrackedRead<UserId, UserSettings> Settings =
        UserStoreResources.Settings.Read(store.GetSettings);

    public readonly TrackedRead<UserId, UserPreferences> Preferences =
        UserStoreResources.Preferences.Read(store.GetPreferences);

    private readonly TrackedWrite<UserId> _updateUser = new(
        sluice,
        UserStoreResources.User.For,
        UserStoreResources.Settings.For,
        UserStoreResources.Preferences.For);

    // Generated write method mirrors the store signature.
    // The work delegate (store call) is hidden inside.
    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct) =>
        _updateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct);
}
```

Queries are still hand-written — the generator cannot compose projections:

```csharp
public sealed class UserQueries(UserStoreSluice users)
{
    public readonly CachedQuery<UserId, UserProfile> Profile =
        new("user.profile", id => id.Value, async (id, scope) =>
        {
            var u = await users.User.Get(id, scope);
            var s = await users.Settings.Get(id, scope);
            var p = await users.Preferences.Get(id, scope);
            return new UserProfile(
                id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
        });
}
```

### Call site

```csharp
var sluice = new SluiceKernel(new InMemoryCacheStore());
var store  = new InMemoryUserStore();
var users  = new UserStoreSluice(sluice, store);     // generated
var queries = new UserQueries(users);                 // hand-written

await sluice.Get(queries.Profile, new UserId("alice"), ct);     // miss
await sluice.Get(queries.Profile, new UserId("alice"), ct);     // hit

// Generated write: mirrors store signature, hides the work delegate.
await users.UpdateUser(new UserId("alice"), new("Alice 2", true, "dark"), ct);

await sluice.Get(queries.Profile, new UserId("alice"), ct);     // miss, fresh
```

---

## Call site comparison

| Operation | Tier 0 (no Sluice) | Tier 1 (hand-written) | Tier 2 (codegen) |
|---|---|---|---|
| **Read** | `store.GetUser(id, ct)` | `users.User.Get(id, scope)` | `users.User.Get(id, scope)` |
| **Query** | `svc.GetProfile(id, ct)` | `sluice.Get(queries.Profile, id, ct)` | `sluice.Get(queries.Profile, id, ct)` |
| **Write** | `store.UpdateUser(id, input, ct)` | `users.UpdateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct)` | `users.UpdateUser(id, input, ct)` |

Reads and queries are identical between Tier 1 and Tier 2. Writes differ: hand-written
exposes `.Write(key, work, ct)` (you supply the store call), codegen exposes a method
that mirrors the store signature (the store call is generated for you).

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
entity:userSettings:alice
collection:orders.byCustomer:customer-123
```

`EntityResource<TKey>.For(key)` and `CollectionResource<TKey>.For(key)` produce addresses.
`Wildcard()` produces an address that matches all keys of a resource (for bulk invalidation):

```csharp
// Invalidate every cached entry that read any user entity.
await sluice.Invalidate(
    new WriteEffect(UserResources.User.Wildcard()),
    ct);
```

---

## Escape Hatches

For one-off writes without a `TrackedWrite` field:

```csharp
// Run the work, then invalidate the declared addresses.
await sluice.Apply(
    ct => store.UpdateUser(id, input, ct),
    new WriteEffect(
        UserResources.User.For(id),
        UserResources.Settings.For(id)),
    ct);

// Or if the work already committed — just invalidate.
await sluice.Invalidate(
    new WriteEffect(UserResources.User.For(id)),
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
var query = new CachedQuery<UserId, User>("user.byId", id => id.Value,
    async (id, scope) =>
    {
        // Manual tracking instead of TrackedRead.
        return await scope.Track(
            new ResourceAddress(ResourceKind.Entity, "user", id.ResourceKey),
            ct => store.GetUser(id, ct));
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
  user.profile:v1:"alice"
    reads:
      entity:user:alice
      entity:userSettings:alice
      entity:userPreferences:alice
    cached: 2026-07-06T10:20:01+00:00

RESOURCE ADDRESSES:
  entity:user:alice
    invalidates:
      user.profile:v1:"alice"
  entity:userSettings:alice
    invalidates:
      user.profile:v1:"alice"
```

Each cached entry lists the resources it tracked. Each resource address lists the entries
it would invalidate.

---

## Codegen Attributes

| Attribute | Emits |
|---|---|
| `[Sluice]` on interface | `{Name}Resources` + `{Name}Sluice` classes |
| `[ReadEntity("name")]` | `EntityResource<TKey>` field + `TrackedRead` |
| `[ReadCollection("name", "byKey")]` | `CollectionResource<TKey>` field + `TrackedRead` |
| `[WriteEntity("name")]` | Address in generated `TrackedWrite` |
| `[WriteCollection("name", "byKey")]` | Address in generated `TrackedWrite` |
| `ResultKey = nameof(X.Id)` (on write attrs) | Result-derived address resolver in `TrackedWrite<TKey, TResult>` |

Resources are canonicalised by name. `[ReadEntity("user")]` and `[WriteEntity("user")]`
produce a single `User` field — not duplicates.

Generated naming: strip the `I` prefix from the interface, append `Sluice`.
`IUserStore` → `UserStoreSluice`. Override with `[Sluice("User")]` → `UserSluice`.
A custom name ending in `Sluice` (e.g. `[Sluice("UserSluice")]`) is treated as invalid
base-name input and emits `SLUICE002` - no source is generated.

---

## Example

```bash
dotnet run --project examples/user-profile
```

Key files:

- `examples/user-profile/Domain.cs` — IDs, DTOs, store port, in-memory store
- `examples/user-profile/UserApi.cs` — resources, UserSluice, UserQueries, use case
- `examples/user-profile/Program.cs` — runnable walkthrough

## Build And Test

```bash
dotnet build Sluice.sln
dotnet test Sluice.sln
```

## Redis Backing

Sluice ships two Redis-backed stores in the separate `Sluice.Redis` package:

- `RedisCacheStore` — replaces `InMemoryCacheStore` for distributed caching. Serializes `CacheEntry<TValue>` as JSON using `System.Text.Json` with configurable `JsonSerializerOptions`.
- `RedisGraphStore` — replaces `InMemoryGraphStore` for distributed invalidation. Stores resource addresses as Redis SET members via `SADD`/`SMEMBERS`/`SREM`.

Primary wiring uses `SluiceRedis.Create`, which automatically wires all three Redis components with a shared circuit breaker for resilience:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("redis://localhost:6379");
var sluice = SluiceRedis.Create(redis);
```

`SluiceRedis.Create` wraps `RedisCacheStore`, `RedisGraphStore`, and `RedisStampedeCoordinator` in resilient decorators that share one circuit breaker. When Redis is unavailable, cache reads return misses, cache writes are silently dropped, and no Redis exceptions propagate to the caller. The circuit opens after 5 consecutive failures and recovers after a 10-second cooldown with a single probe call.

For custom configurations (different key prefix, serializer options, stampede options, or circuit breaker settings), construct the stores manually:

```csharp
var redis = await ConnectionMultiplexer.ConnectAsync("redis://localhost:6379");
var cacheStore = new RedisCacheStore(redis, "sluice");
var graphStore = new RedisGraphStore(redis, "sluice");
var stampede = new RedisStampedeCoordinator(redis);
var sluice = new SluiceKernel(cacheStore, graphStore, stampedeCoordinator: stampede);
```

Both `RedisCacheStore` and `RedisGraphStore` must be passed together to `SluiceKernel` for distributed deployments. Passing only `RedisCacheStore` produces distributed cache + in-process graph — a hybrid that causes invalidation misses across processes.

Stampede protection coalesces redundant recompute across processes. When multiple processes miss the same cache entry simultaneously, only one computes while others poll the cache with backoff until the entry appears. The `RedisStampedeCoordinator` uses `SET NX PX` for lease acquisition and an atomic Lua compare-and-delete for release. The Lua constraint (no Lua scripts in Redis backing) is relaxed specifically for this release script — `RedisCacheStore` and `RedisGraphStore` remain Lua-free. Lease TTL (default 30s) bounds how long one process may own a recompute; it is separate from cache entry TTL, which may be minutes or hours.

The caller owns the `ConnectionMultiplexer` and its disposal lifecycle. Sluice does not dispose it.

Serialization uses `System.Text.Json` with default options. Pass `JsonSerializerOptions` to the `RedisCacheStore` constructor for camelCase property naming, string-valued enums, or other customization:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};
var cacheStore = new RedisCacheStore(redis, "sluice", options);
```

Topology: single-node or managed endpoint (e.g. Azure Cache for Redis non-cluster mode, AWS ElastiCache non-cluster mode). Primary/replica managed endpoints are acceptable — `StackExchange.Redis` routes writes such as `KeyDeleteAsync` to the primary. Redis Cluster is not supported — `SCAN`-based clear and flush assume single-node key distribution.

Consistency is eventual. TTL is the backstop for distributed recompute and invalidation races.

For multi-tenant isolation on a shared Redis, use matching `keyPrefix` parameters (default `"sluice"`) on both `RedisCacheStore` and `RedisGraphStore`.

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

## Limitations

- Invalidation only knows about reads through `TrackedRead.Get` or `scope.Track`.
- Writes must declare every resource address they changed. Missing addresses leave stale entries.
- If a write succeeds and the process crashes before invalidation, stale entries remain until TTL or flush.
- In-memory cache/graph stores are for tests and single-process apps.
- Distributed stampede prevention coalesces redundant recompute but does not prevent all cross-process invalidation races. Those remain bounded by TTL unless distributed epoch fencing is added (future work).
- Redis backing is single-node or managed endpoint only. Redis Cluster topology is not supported.
- The dependency graph is process-local. Cross-process invalidation requires Redis backing.
- Benchmark overhead is dominated by fixed tracking cost for tiny in-memory operations; Sluice is intended for cached operations expensive enough to justify dependency tracking.
