# Sluice

Sluice is a small .NET library for cache invalidation by observed dependencies.

Instead of invalidating cached operations with broad tags, a query records the resource addresses it actually read. Later, writes declare the resource addresses they changed. Sluice evicts only cached entries whose observed reads intersect those changed addresses.

The app-facing API is deliberately small:

- `sluice.Get(query, key, ct)` reads through the cache.
- `sluice.Apply(work, writeEffect, ct)` runs a write and invalidates affected cached entries.
- `TrackedRead<TKey, TValue>` bundles a resource address with its store read delegate — `.Get(key, scope)` records the dependency and fetches the value.
- `new WriteEffect(address)` declares what a write changed.
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

Bundle each resource with its store read delegate — `TrackedRead` handles tracking automatically:

```csharp
// Each TrackedRead is a one-liner: resource + store delegate.
// The store is captured in the closure — no DI into the read itself.
// .Get(key, scope) records the dependency and runs the store call.
// The cancellation token from IReadScope is passed to the store delegate internally.
public sealed class UserReads(IUserStore store)
{
    public readonly TrackedRead<UserId, User> User = new(
        UserResources.User.For, (id, ct) => store.GetUser(id, ct));

    public readonly TrackedRead<UserId, UserSettings> Settings = new(
        UserResources.Settings.For, (id, ct) => store.GetSettings(id, ct));

    public readonly TrackedRead<UserId, UserPreferences> Preferences = new(
        UserResources.Preferences.For, (id, ct) => store.GetPreferences(id, ct));
}
```

Define a query — the compute body reads through the tracked reads, no Sluice vocabulary at the read site:

```csharp
public sealed class UserQueries(UserReads reads)
{
    public readonly Query<UserId, UserProfile> Profile = new(
        "user.profile",
        id => id.Value,
        async (id, scope) =>
        {
            var user = await reads.User.Get(id, scope);
            var settings = await reads.Settings.Get(id, scope);
            var preferences = await reads.Preferences.Get(id, scope);

            return new UserProfile(
                id,
                user.Name,
                user.Email,
                settings.DarkMode,
                settings.Language,
                preferences.Theme);
        });
}
```

Read through Sluice — first call computes and caches, second call returns the cached value:

```csharp
var sluice = new SluiceKernel(new InMemoryCacheStore());
var reads = new UserReads(store);
var queries = new UserQueries(reads);

// Cache miss on first call: runs Compute, tracks reads, stores the result.
// Cache hit on second call with the same key: returns immediately, no store calls.
var profile = await sluice.Get(queries.Profile, new UserId("alice"), ct);
```

Declare changed resources on writes — Sluice runs the write, then evicts any cached entry whose tracked reads intersect the changed addresses:

```csharp
public sealed record UpdateUserInput(string Name, bool DarkMode, string Theme);

public static class UserWriteEffects
{
    public static WriteEffect Updated(UserId id) =>
        new(
            UserResources.User.For(id),
            UserResources.Settings.For(id),
            UserResources.Preferences.For(id));
}

public sealed class UpdateUserUseCase(ISluice sluice, IUserStore store)
{
    // One application use case can update multiple backing records.
    // The WriteEffect recipe is the invalidation contract for the use case.
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        sluice.Apply(
            ct => store.UpdateUser(id, input, ct),
            UserWriteEffects.Updated(id),
            ct);
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

`ISluice.Apply` runs the write first, then invalidates affected cached entries before returning. The primary form takes a pre-built `WriteEffect`; the `Action<ChangeBuilder>` overload is inline syntax for the same thing.

The recommended pattern is to pre-build `WriteEffect` recipes in a static class. This names the intent of each write and keeps call sites readable:

```csharp
// WriteEffect recipes encapsulate the resource addresses a use case changes.
// One recipe per use case, grouped in a static class for discoverability.
public static class UserWriteEffects
{
    // Each address is a resource the write changed.
    // Entries that read ANY of these addresses are evicted.
    public static WriteEffect Updated(UserId id) =>
        new(
            UserResources.User.For(id),
            UserResources.Settings.For(id),
            UserResources.Preferences.For(id));
}
```

Call sites pass the recipe as the second argument to `Apply`:

```csharp
public sealed record UpdateUserInput(string Name, bool DarkMode, string Theme);

public sealed class UpdateUserUseCase(ISluice sluice, IUserStore store)
{
    // The store call is the write. It can update multiple backing rows.
    // The WriteEffect recipe tells Sluice which resources those rows represent.
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        sluice.Apply(
            ct => store.UpdateUser(id, input, ct),
            UserWriteEffects.Updated(id),
            ct);
}
```

The cached profile is one query, but it reads several resources. The update is one use case, but it changes several resources. The `WriteEffect` recipe is the explicit bridge between those two facts.

For result-derived changed addresses — the address depends on what the write returned (e.g. a database-generated ID), use `WriteEffect<T>`:

```csharp
// WriteEffect<T> takes static addresses as constructor params,
// then .ChangesResult() adds result-derived resolvers that run after the write completes.
public static class OrderWriteEffects
{
    // Declares a static address (the collection) AND a result-derived one
    // (the individual order, whose ID isn't known until the store assigns it).
    public static WriteEffect<Order> Created(CustomerId customerId) =>
        new WriteEffect<Order>(OrderResources.OrdersByCustomer.For(customerId))
            .ChangesResult(order => OrderResources.Order.For(order.Id));
}

// The generic Apply<T> returns the write's result so the caller can use it.
var order = await sluice.Apply(
    ct => store.CreateOrder(customerId, input, ct),
    OrderWriteEffects.Created(customerId),
    ct);
```

For dynamic cases where addresses depend on runtime conditions, use `Action<ChangeBuilder>` as an escape hatch:

```csharp
// The Action<ChangeBuilder> overload is inline syntax for building a WriteEffect.
// Use it for one-off writes where a named recipe would be overkill.
await sluice.Apply(
    _ => store.UpdateUserName(id, "Alice Smith"),
    changes => changes.Changed(UserResources.User.For(id)),
    ct);
```

Reads done during writes are not tracked. If a write needs existing state to declare the correct changed addresses, read it from the store directly before calling `Apply`.

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
- `examples/user-profile/UserApi.cs` — resources, tracked reads, queries, write-effect recipe, use case
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
