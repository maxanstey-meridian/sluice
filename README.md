# Sluice

Sluice is a small .NET library for cache invalidation by observed dependencies.

Instead of invalidating cached operations with broad tags, a query records the resource addresses it actually read. Later, writes declare the resource addresses they changed. Sluice evicts only cached entries whose observed reads intersect those changed addresses.

The app-facing API is deliberately small:

- `sluice.Get(query, key, ct)` reads through the cache.
- `sluice.Apply(work, changes, ct)` runs a write and invalidates affected cached entries.
- `read.Track(address, work)` records a dependency inside a query.
- `changes.Changed(address)` declares what a write changed.

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

Example: a user profile query reads `entity:user:alice` and `entity:userSettings:alice`. Updating the settings evicts the profile entry (which read settings), but keeps the user entry (which didn't).

## Quick Start

Create resources — these declare the identity of the data you cache around:

```csharp
// Keys implement IResourceKey so that resource addresses can use their ToString().
public sealed record UserId(string Value) : IResourceKey
{
    public override string ToString() => Value;
}

// Each resource is a named entity. The name becomes part of the resource address
// (e.g. entity:user:alice). Writes declare these same addresses when they change data.
public static class UserResources
{
    public static readonly EntityResource<UserId> User =
        Resource.Entity<UserId>("user");

    public static readonly EntityResource<UserId> Settings =
        Resource.Entity<UserId>("userSettings");
}
```

Define a query — a cached operation that tracks what it reads:

```csharp
// The store dependency is captured in the closure — no DI into the query itself.
public sealed class UserQueries(IUserStore store)
{
    // A Query takes a name (used in cache keys and the dependency graph),
    // a key selector (serialized to JSON for the cache entry key),
    // and a compute body that does the actual work.
    public readonly Query<UserId, UserProfile> Profile =
        new Query<UserId, UserProfile>("user.profile")
            .Key(id => id.Value)
            .Compute(async (id, read) =>
            {
                // read.Track does two things: records that this cached entry
                // depends on the given resource address, then runs the store call.
                // If a later write changes entity:user:{id}, this entry is evicted.
                var user = await read.Track(
                    UserResources.User.For(id),
                    _ => store.GetUser(id));

                // A second tracked read. The entry now depends on both addresses.
                // Changing either one will evict it.
                var settings = await read.Track(
                    UserResources.Settings.For(id),
                    _ => store.GetSettings(id));

                return new UserProfile(
                    id,
                    user.Name,
                    user.Email,
                    settings.DarkMode,
                    settings.Language);
            });
}
```

Read through Sluice — first call computes and caches, second call returns the cached value:

```csharp
// SluiceKernel wraps an OperationRegistry with an in-memory cache.
var sluice = new SluiceKernel(new InMemoryCacheStore());
var queries = new UserQueries(store);

// Cache miss on first call: runs Compute, tracks reads, stores the result.
// Cache hit on second call with the same key: returns immediately, no store calls.
var profile = await sluice.Get(queries.Profile, new UserId("alice"), ct);
```

Declare changed resources on writes — Sluice runs the write, then evicts any cached entry whose tracked reads intersect the changed addresses:

```csharp
public sealed class UserCommands(ISluice sluice, IUserStore store)
{
    // The first arg is the write itself (the store mutation).
    // The second arg declares which resource address changed.
    // After the write completes, Sluice evicts every cached entry that
    // tracked a read on entity:userSettings:{id}.
    public Task UpdateDarkMode(UserId id, bool darkMode, CancellationToken ct) =>
        sluice.Apply(
            _ => store.UpdateDarkMode(id, darkMode),
            changes => changes.Changed(UserResources.Settings.For(id)),
            ct);
}
```

When dark mode changes, the profile entry (which read `entity:userSettings:alice`) is evicted. The user entry (which only read `entity:user:alice`) stays cached.

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
collection:orders.byCustomer:customer-123
external:stripe:price-list
```

Resource keys implement `IResourceKey`. `EntityResource<TKey>.For(key)` and `CollectionResource<TKey>.For(key)` use `key.ToString()` as the address key.

### Queries

`Query<TKey, TValue>` defines a cached operation.

- `.Key(...)` builds the cache key shape. Sluice serializes it with `System.Text.Json`.
- `.Compute(...)` receives an `IReadScope`.
- Calls to `read.Track(...)` record observed dependencies before running the underlying read.

Queries are registered lazily when first passed to `SluiceKernel.Get`. That means `Describe()` is empty until a query has been executed at least once.

### Writes

`ISluice.Apply` runs the write first, then invalidates affected cached entries before returning.

For static changed addresses — you know the address at call time, no result needed:

```csharp
await sluice.Apply(
    // The write: mutate the store.
    _ => store.UpdateUserName(id, "Alice Smith"),
    // The declaration: this address changed. Any cached entry that
    // tracked a read on entity:user:{id} is evicted.
    changes => changes.Changed(UserResources.User.For(id)),
    ct);
```

For result-derived changed addresses — the address depends on what the write returned (e.g. a database-generated ID):

```csharp
// The resolver runs after the write completes, receiving its result.
// Here the collection address is known upfront, but the entity address
// needs the created order's ID from the store result.
var order = await sluice.Apply(
    ct => store.CreateOrder(customerId, input),
    changes => changes
        .Changed(OrderResources.OrdersByCustomer.For(customerId))
        .Changed(result => OrderResources.Order.For(result.Id)),
    ct);
```

Reads done during writes are not tracked. If a write needs existing state to declare the correct changed addresses, read it from the store directly before calling `Apply`.

### Wildcards

Entity and collection resources support wildcard addresses — useful when a write affects all instances of a resource at once (e.g. a bulk import or schema migration):

```csharp
// Invalidates every cached entry that tracked any read on entity:user:*
UserResources.User.Wildcard()

// Invalidates every cached entry that tracked any read on entity:userSettings:*
UserResources.Settings.Wildcard()
```

A wildcard changed address invalidates all cached entries that read the same resource kind and name, regardless of key.

## Observability

`SluiceKernel` exposes the registry inspection helpers:

```csharp
// Text snapshot of the runtime dependency graph — entries, their tracked reads,
// and which addresses invalidate which entries. Useful for debugging.
var graph = sluice.DumpGraph();

// Static metadata for registered operations — name, input type, output type.
// No execution needed; works off registration alone.
var manifest = sluice.Describe();
```

`DumpGraph()` returns a text snapshot of the runtime state. Example output after reading the user and profile queries for Alice and Bob:

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
    cached: 2026-07-04T13:36:52+00:00

  user.profile:v1:"bob"
    reads:
      entity:user:bob
      entity:userSettings:bob
    cached: 2026-07-04T13:36:52+00:00

RESOURCE ADDRESSES:
  entity:user:alice
    invalidates:
      user.byId:v1:"alice"
      user.profile:v1:"alice"

  entity:userSettings:alice
    invalidates:
      user.profile:v1:"alice"

  entity:user:bob
    invalidates:
      user.profile:v1:"bob"

  entity:userSettings:bob
    invalidates:
      user.profile:v1:"bob"
```

The graph reads top-to-bottom: each cached entry lists the resource addresses it tracked during computation, and each resource address lists the entries it would invalidate. You can see at a glance that changing `entity:userSettings:alice` only hits the profile entry, not the user entry.

`Describe()` returns a `SystemManifest` containing registered operation metadata:

```text
Operations:
  user.byId       UserId → User          (DelegateCachedOperation`2)
  user.profile    UserId → UserProfile   (DelegateCachedOperation`2)
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
- a composite query reading two resources
- graph output via `DumpGraph()`
- selective invalidation: updating settings evicts the profile but not the user entry

Key files:

- `examples/user-profile/Domain.cs` — IDs, DTOs, store port, in-memory store
- `examples/user-profile/UserApi.cs` — resources, queries, commands
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
- `IReadScope`
- `ChangeBuilder`
- `ChangeBuilder<T>`
- `Resource`
- `EntityResource<TKey>`
- `CollectionResource<TKey>`
- `ExternalResource`
- `ResourceAddress`
- `IResourceKey`

Kernel types still exist and are tested directly:

- `CachedOperation<TKey, TValue>`
- `OperationRegistry`
- `OperationContext`
- `ChangeContext`
- `WriteEffect`
- `WriteEffect<T>`
- `TrackedResource`
- `ICacheStore`
- `InMemoryCacheStore`

The overlay is the intended application API. The kernel remains useful for lower-level testing and future integrations.

## Limitations

- Invalidation only knows about reads that go through `read.Track`.
- If a write succeeds and the process crashes before invalidation completes, stale cache entries can remain.
- The in-memory cache store is for tests/examples, not distributed production caching.
- The dependency graph is process-local.
- Benchmark overhead is dominated by fixed tracking cost for tiny in-memory operations; Sluice is intended for cached operations expensive enough to justify dependency tracking.
