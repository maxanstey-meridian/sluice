# Sluice

Sluice is a small .NET library for cache invalidation by observed dependencies.

Instead of invalidating cached operations with broad tags, a query records the resource addresses it actually read. Later, writes declare the resource addresses they changed. Sluice evicts only cached entries whose observed reads intersect those changed addresses.

The app-facing API is deliberately small:

- `sluice.Get(query, key, ct)` reads through the cache.
- `TrackedRead<TKey, TValue>` bundles a resource address with its store read delegate — `.Get(key, scope)` records the dependency and fetches the value.
- `TrackedWrite<TKey>` captures `ISluice` + resource address delegates — `.Write(key, work, ct)` runs the write and invalidates affected cached entries.
- `sluice.Apply(work, writeEffect, ct)` is the lower-level write path when you don't have a `TrackedWrite` — runs a write and invalidates affected cached entries.
- `new WriteEffect(address)` declares what a write changed (used with `sluice.Apply`).
- `changes.Changed(address)` is the inline escape hatch for one-off writes.

## Status

Prototype / proof of concept.

Current target framework: `.NET 10` (`net10.0`).

The library project has no third-party package references.

## Why

Manual cache invalidation tends to drift because the cache key and the invalidation site are maintained separately.

Sluice makes the relationship explicit:

- A query tracks the precise resources it read while computing a value.
- A write declares the precise resources it changed.
- The registry maintains a dependency graph from resource addresses to cached entries.
- Invalidation is selective: unchanged dependencies stay cached.

Example: a user profile query reads `entity:user:alice`, `entity:userSettings:alice`, and `entity:userPreferences:alice`. A user update use case declares the resources it changed, and Sluice evicts only cached entries that actually read one of those addresses.

## Quick Start

Create resources — these declare the identity of the data you cache around:

```csharp
// Keys implement IResourceKey. The ResourceKey property is used in resource
// addresses — it must be stable, culture-invariant, and unique within the resource.
public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

// Resources are static — pure data, no store dependency.
// The name becomes part of the resource address (e.g. entity:user:alice).
// Writes reference these same resources when declaring what changed.
public static class UserResources
{
    public static readonly EntityResource<UserId> User =
        Resource.Entity<UserId>("user");

    public static readonly EntityResource<UserId> Settings =
        Resource.Entity<UserId>("userSettings");

    public static readonly EntityResource<UserId> Preferences =
        Resource.Entity<UserId>("userPreferences");
}
```

Bundle resources, reads, queries, and writes into one `<Domain>Sluice` class. A static `Register` factory makes the declaration read like registration, not instantiation:

```csharp
// The domain sluice: one class that declares what exists, how to read it,
// how to compose it, and what writes invalidate.
// Private constructor — callers use Register.
public sealed class UserSluice
{
    public TrackedRead<UserId, User> User { get; }
    public TrackedRead<UserId, UserSettings> Settings { get; }
    public TrackedRead<UserId, UserPreferences> Preferences { get; }
    public Query<UserId, UserProfile> Profile { get; }
    public TrackedWrite<UserId> UpdateUser { get; }

    private UserSluice(
        TrackedRead<UserId, User> user,
        TrackedRead<UserId, UserSettings> settings,
        TrackedRead<UserId, UserPreferences> preferences,
        Query<UserId, UserProfile> profile,
        TrackedWrite<UserId> updateUser)
    {
        User = user;
        Settings = settings;
        Preferences = preferences;
        Profile = profile;
        UpdateUser = updateUser;
    }

    public static UserSluice Register(ISluice sluice, IUserStore store)
    {
        // Reads — method groups, not lambdas. Each line registers a cached
        // read backed by the store, tracked against the resource address.
        var user = new TrackedRead<UserId, User>(
            UserResources.User.For, store.GetUser);
        var settings = new TrackedRead<UserId, UserSettings>(
            UserResources.Settings.For, store.GetSettings);
        var preferences = new TrackedRead<UserId, UserPreferences>(
            UserResources.Preferences.For, store.GetPreferences);

        // Query — cached composition of tracked reads.
        var profile = new Query<UserId, UserProfile>(
            "user.profile",
            id => id.Value,
            async (id, scope) =>
            {
                var u = await user.Get(id, scope);
                var s = await settings.Get(id, scope);
                var p = await preferences.Get(id, scope);

                return new UserProfile(
                    id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
            });

        // Write — TrackedWrite captures ISluice + address delegates.
        // .Write(key, work, ct) resolves the key into addresses, builds a WriteEffect,
        // and calls sluice.Apply internally. The use case never sees the WriteEffect.
        var updateUser = new TrackedWrite<UserId>(
            sluice,
            UserResources.User.For,
            UserResources.Settings.For,
            UserResources.Preferences.For);

        return new(user, settings, preferences, profile, updateUser);
    }
}
```

Read through Sluice — first call computes and caches, second call returns the cached value:

```csharp
var sluice = new SluiceKernel(new InMemoryCacheStore());
var users = UserSluice.Register(sluice, store);

// Cache miss on first call: runs Compute, tracks reads, stores the result.
// Cache hit on second call with the same key: returns immediately, no store calls.
var profile = await sluice.Get(users.Profile, new UserId("alice"), ct);
```

Write through the TrackedWrite field — one line, no WriteEffect at the call site:

```csharp
public sealed record UpdateUserInput(string Name, bool DarkMode, string Theme);

public sealed class UpdateUserUseCase(UserSluice users, IUserStore store)
{
    // The use case passes the runtime value (id) and the store call (work).
    // TrackedWrite handles the WriteEffect internally.
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        users.UpdateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct);
}
```

When the use case changes Alice, Sluice evicts any cached entry that read `entity:user:alice`, `entity:userSettings:alice`, or `entity:userPreferences:alice`.

## Concepts

### Resource Addresses

A `ResourceAddress` is the unit of dependency tracking.

It has three parts:

- `ResourceKind`: `Entity`, `Collection`, or `External`
- `Name`: the resource name, such as `product` or `orders.byCustomer`
- `Key`: the specific resource key

The string form is:

```text
entity:user:alice
entity:userSettings:alice
entity:userPreferences:alice
collection:orders.byCustomer:customer-123
external:stripe:price-list
```

Resource keys implement `IResourceKey`, which exposes `ResourceKey` — a string that uniquely identifies the key within its resource type. `EntityResource<TKey>.For(key)` and `CollectionResource<TKey>.For(key)` use `key.ResourceKey` as the address key. `ResourceKey` must be stable (same value across process restarts), culture-invariant, and unique within the resource.

### Queries

`Query<TKey, TValue>` defines a cached operation.

- The **name** (first arg) is used in cache keys and the dependency graph.
- The **key selector** (second arg) builds the cache key shape. Sluice serializes it with `System.Text.Json`.
- The **compute body** (third arg) receives an `IReadScope`.
- Optional `version` and `ttl` parameters control cache invalidation and entry expiry.

`TrackedRead<TKey, TValue>` is the recommended way to read inside a compute body. It bundles a resource address with its store delegate — `.Get(key, scope)` records the dependency and runs the store call. The cancellation token from `IReadScope` is passed to the store delegate internally; there's no `ct` parameter at the call site.

For custom tracking (computed addresses, conditional reads), `read.Track(...)` is available directly on `IReadScope` as an escape hatch.

Queries are registered lazily when first passed to `SluiceKernel.Get`. That means `Describe()` is empty until a query has been executed at least once.

### Writes

The recommended path is `TrackedWrite<TKey>` — a field on your `<Domain>Sluice` class that captures `ISluice` and the resource address delegates. The use case calls `.Write(key, work, ct)`, which resolves the key into addresses, builds a `WriteEffect` internally, and calls `sluice.Apply`:

```csharp
// TrackedWrite field — declared in UserSluice.Register.
var updateUser = new TrackedWrite<UserId>(
    sluice,
    UserResources.User.For,
    UserResources.Settings.For,
    UserResources.Preferences.For);

// Call site — the use case passes the key and the store call.
// TrackedWrite handles the WriteEffect. One line.
public sealed class UpdateUserUseCase(UserSluice users, IUserStore store)
{
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        users.UpdateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct);
}
```

For writes without a `TrackedWrite` field, use `sluice.Apply` directly with a `WriteEffect`:

```csharp
// The WriteEffect lists every resource address the write changed.
// Sluice runs the work first, then evicts cached entries whose tracked reads
// intersect any of these addresses.
await sluice.Apply(
    ct => store.UpdateUser(id, input, ct),
    new WriteEffect(
        UserResources.User.For(id),
        UserResources.Settings.For(id),
        UserResources.Preferences.For(id)),
    ct);
```

For dynamic cases where addresses depend on runtime conditions, use `Action<ChangeBuilder>` as an inline escape hatch:

```csharp
// Action<ChangeBuilder> is inline syntax for building a WriteEffect.
// Use for one-off writes where a TrackedWrite field would be overkill.
await sluice.Apply(
    _ => store.UpdateUserName(id, "Alice Smith"),
    changes => changes.Changed(UserResources.User.For(id)),
    ct);
```

For result-derived changed addresses — the address depends on what the write returned (e.g. a database-generated ID), use `WriteEffect<T>`:

```csharp
// WriteEffect<T> takes static addresses as constructor params,
// then .ChangesResult() adds result-derived resolvers that run after the write completes.
var effect = new WriteEffect<Order>(OrderResources.OrdersByCustomer.For(customerId))
    .ChangesResult(order => OrderResources.Order.For(order.Id));

// The generic Apply<T> returns the write's result so the caller can use it.
var order = await sluice.Apply(
    ct => store.CreateOrder(customerId, input, ct),
    effect,
    ct);
```

Reads done during writes are not tracked. If a write needs existing state to declare the correct changed addresses, read it from the store directly before calling `Apply` or inside the work delegate.

### Wildcards

Entity and collection resources support wildcard addresses — useful when a write affects all instances of a resource at once (e.g. a bulk import or schema migration):

```csharp
// A wildcard address has "*" as the key segment.
// It matches any cached entry that read the same resource kind and name,
// regardless of the specific key value.
UserResources.User.Wildcard()        // entity:user:*
UserResources.Settings.Wildcard()    // entity:userSettings:*
UserResources.Preferences.Wildcard() // entity:userPreferences:*

// Use in a WriteEffect for bulk invalidation:
new WriteEffect(UserResources.User.Wildcard())
```

A wildcard changed address invalidates all cached entries that read the same resource kind and name, regardless of key.

## Observability

`SluiceKernel` exposes the registry inspection helpers:

```csharp
// Text snapshot of the runtime dependency graph — entries, their tracked reads,
// and which addresses invalidate which entries. Useful for debugging.
var graph = sluice.DumpGraph();

// Static metadata for lazily registered operations — name, input type, output type.
// Queries appear here after their first Get call.
var manifest = sluice.Describe();
```

`DumpGraph()` returns a text snapshot of the runtime state. Example output after reading the user query for Alice and profile queries for Alice and Bob:

```text
OPERATIONS:
  user.byId:v1:"alice"
    reads:
      entity:user:alice
    cached: 2026-07-04T13:36:52+00:00

  user.profile:v1:"alice"
    reads:
      entity:user:alice
      entity:userSettings:alice
      entity:userPreferences:alice
    cached: 2026-07-04T13:36:52+00:00

  user.profile:v1:"bob"
    reads:
      entity:user:bob
      entity:userSettings:bob
      entity:userPreferences:bob
    cached: 2026-07-04T13:36:52+00:00

RESOURCE ADDRESSES:
  entity:user:alice
    invalidates:
      user.byId:v1:"alice"
      user.profile:v1:"alice"

  entity:userSettings:alice
    invalidates:
      user.profile:v1:"alice"

  entity:userPreferences:alice
    invalidates:
      user.profile:v1:"alice"

  entity:user:bob
    invalidates:
      user.profile:v1:"bob"

  entity:userSettings:bob
    invalidates:
      user.profile:v1:"bob"

  entity:userPreferences:bob
    invalidates:
      user.profile:v1:"bob"
```

The graph reads top-to-bottom: each cached entry lists the resource addresses it tracked during computation, and each resource address lists the entries it would invalidate. You can see at a glance that `user.profile:v1:"alice"` depends on three backing resources, while `user.byId:v1:"alice"` only depends on the user row.

`Describe()` returns a `SystemManifest` containing registered operation metadata:

```text
Operations:
  user.byId       UserId → User          (delegate-backed query)
  user.profile    UserId → UserProfile   (delegate-backed query)
```

Because overlay queries are lazily registered, `Describe()` only includes queries that have been executed through `Get`.

## Example

Run the user profile example:

```bash
dotnet run --project examples/user-profile/SluiceExample.csproj
```

The example demonstrates:

- first read as a cache miss
- repeated read as a cache hit
- a composite query reading three resources
- graph output via `DumpGraph()`
- use-case invalidation: one write changes multiple backing resources

Key files:

- `examples/user-profile/Domain.cs` — IDs, DTOs, store port, in-memory store
- `examples/user-profile/UserApi.cs` — resources, UserSluice (reads, queries, writes), use case
- `examples/user-profile/Program.cs` — runnable walkthrough

## Build And Test

```bash
dotnet build Sluice.sln
dotnet test Sluice.sln --no-build
```

Or with Task:

```bash
task build
task test
```

## Current API Surface

Primary app-facing types:

- `SluiceKernel`
- `ISluice`
- `Query<TKey, TValue>`
- `TrackedRead<TKey, TValue>`
- `TrackedWrite<TKey>`
- `IReadScope`
- `WriteEffect`
- `WriteEffect<T>`
- `Resource`
- `EntityResource<TKey>`
- `CollectionResource<TKey>`
- `ExternalResource`
- `ResourceAddress`
- `IResourceKey`

Inline/escape-hatch types:

- `ChangeBuilder`
- `ChangeBuilder<T>`

Kernel types still exist and are tested directly:

- `CachedOperation<TKey, TValue>`
- `OperationRegistry`
- `OperationContext`
- `ChangeContext`
- `TrackedResource`
- `ICacheStore`
- `InMemoryCacheStore`

The overlay is the intended application API. The kernel remains useful for lower-level testing and future integrations.

## Limitations

- Invalidation only knows about reads that go through `TrackedRead.Get` or `read.Track`.
- Writes must declare every resource address they changed. Missing write effects leave stale cached entries.
- If a write succeeds and the process crashes before invalidation completes, stale cache entries can remain.
- The in-memory cache store is for tests/examples, not distributed production caching.
- The dependency graph is process-local.
- Benchmark overhead is dominated by fixed tracking cost for tiny in-memory operations; Sluice is intended for cached operations expensive enough to justify dependency tracking.
