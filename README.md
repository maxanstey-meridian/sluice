<p align="center">
  <h1 align="center">Sluice</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Meridian.Sluice"><img src="https://img.shields.io/nuget/v/Meridian.Sluice?label=Meridian.Sluice" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/Meridian.Sluice.EntityFrameworkCore"><img src="https://img.shields.io/nuget/v/Meridian.Sluice.EntityFrameworkCore?label=Meridian.Sluice.EntityFrameworkCore" alt="NuGet" /></a>
    <img src="https://img.shields.io/badge/license-MIT-blue" alt="License" />
  </p>
</p>

**Your reads are your invalidation rules.** Sluice is a cache for .NET that knows what each
cached value was built from. Cache a user's dashboard, and Sluice notes that it read the user's
row and the dark-mode flag. Toggle the flag and every dashboard that read it is thrown away.
Change one user's greeting and only that user's dashboard goes. You never keep a list of cache
keys to clear.

[FusionCache](https://github.com/ZiggyCreatures/FusionCache) does the actual caching: in
memory, in Redis, and keeping several servers in sync. Sluice adds the "what did this read?"
part. With EF Core you write ordinary queries: Sluice records the entity types each one reads,
and saving your changes clears the right entries by itself. With any other data source, you tell
Sluice what changed after you write it.

## The rules

```text
Name each thing that can change, once.
A cached value records what it read while it's built.
A write clears the cached values that read what it changed.
Clearing waits until the write has committed.
```

## Prerequisites

- .NET 10.
- FusionCache 2.9 or later. Sluice brings it in.
- For `Meridian.Sluice.EntityFrameworkCore`: EF Core 10.0.12 or later on a relational provider.
  It's tested on PostgreSQL; the playground runs on SQLite.

## Install

```bash
dotnet add package Meridian.Sluice
dotnet add package Meridian.Sluice.EntityFrameworkCore   # only if you use EF Core
```

The two share a version number, and the EF Core package needs exactly its own version of
`Meridian.Sluice`, so upgrade them together.

## Three steps in

The code below is the
[playground](https://github.com/maxanstey-meridian/sluice/tree/main/examples/playground)'s: a
dashboard for Alice, an admin, and Bob, a member. Both read their user row and a dark-mode flag.
Only Alice reads a greeting, which comes from a service that isn't EF. Everything Sluice has is
in the `Sluice` namespace.

### 1. Declare what can change, and what you cache

```csharp
builder.Services.AddSluice();
```

```csharp
using Sluice;

public sealed record UserId(string Value);
public sealed record Dashboard(string Name, bool DarkMode, string? Greeting);

public static class Dashboards
{
    // Something a cached value can depend on: one user's greeting.
    public static readonly Source<UserId> Greeting = new("greeting", id => id.Value);

    // Something cached: a user's dashboard, kept for five minutes unless invalidated.
    public static readonly Query<UserId, Dashboard> ForUser = new("dashboard", id => id.Value)
    {
        Duration = TimeSpan.FromMinutes(5),
    };
}
```

A `Source` names something that can change. Reads record it and writes clear it, and both use
this one declaration, so they can't disagree on a name. A `Query` names something cached, with
its key and value types, so the compiler checks the key and value of every call. Sluice needs
each key as text, and `UserId` isn't a string, so each declaration says how to turn it into one
(`id => id.Value`). Strings, numbers, `Guid`s and enums need nothing. `Duration` is optional:
without it, the default you set in `AddSluice` applies. A value with no key, such as a list of
everything, is a `Query<TValue>`.

### 2. Cache a read

Inject `SluiceCache`, which `AddSluice` registers, and cache through it:

```csharp
public ValueTask<Dashboard> GetDashboard(UserId userId, CancellationToken ct) =>
    cache.GetOrSetAsync(
        Dashboards.ForUser,
        userId,
        async (read, ct) =>
        {
            // EF Core: query the compute's own context, not an injected one.
            // Each query records what it reads.
            var computeDb = read.Db<AppDb>();
            var user = await computeDb.Users.SingleAsync(u => u.Id == userId, ct);
            var darkMode = await computeDb.Flags
                .Where(flag => flag.Id == FlagId.DarkMode)
                .Select(flag => flag.Enabled)
                .SingleAsync(ct);

            // Any other source: record the dependency and fetch in one call.
            // Only admins read a greeting, so only admins depend on one.
            string? greeting = null;
            if (user.Role == Role.Admin)
            {
                greeting = await read.From(
                    Dashboards.Greeting.For(userId),
                    ct => greetings.GetAsync(userId, ct)
                );
            }

            return new Dashboard(user.Name, darkMode, greeting);
        },
        ct
    );
```

The lambda builds the dashboard when it isn't cached; Sluice calls it the compute. Everything
it reads is recorded. `read.From(dependency, fetch)` works with any data source: it records the
dependency and runs the fetch in one call, so you can't record one thing and fetch another.
EF Core is covered below.

After both dashboards are cached, Alice's depends on the users, the flags and her greeting.
Bob's depends on the users and the flags: he isn't an admin, so his compute never read a
greeting.

### 3. Invalidate once the write has committed

```csharp
public async Task SetGreeting(UserId userId, string text)
{
    await greetings.SetAsync(userId, text);

    // Not EF, so say what changed, after the write.
    await cache.InvalidateAsync(Dashboards.Greeting.For(userId));
}
```

Only Alice's dashboard read Alice's greeting, so only hers is thrown away. Bob's stays cached.
Invalidate after the write commits, never before. Otherwise another request could rebuild the
dashboard from the old data in between and cache it again.
Keep the invalidation inside the method that writes, as here, and have every caller go through
it: then no write can forget it.

## With EF Core

```csharp
builder.Services.AddDbContextFactory<AppDb>(
    (services, options) => options.UseSqlite(connectionString).UseSluice(services)
);
```

`UseSluice` connects the context to Sluice. Register it however you like (`AddDbContext`,
`AddDbContextFactory` or a pool): each compute takes an `AppDb` of its own from a DI scope it
opens for itself.

### Reading

Inside a compute, `read.Db<AppDb>()` gives you that context. Query it as you would any context:
filters, projections, `Include`, navigations, joins, subqueries, many-to-many, `Find`, compiled
queries. Sluice records every entity type each query reads, and a write to any of them evicts
the value. The compute's context is fresh, tracks nothing by default, and isn't in your request's
transaction, so it reads what's committed and nothing else. An entity type is a class in your
EF model, not a table: a second class mapped to the same table, such as a read model in
another context, isn't linked to it, so a write through one doesn't evict reads through the
other. See [Limitations](https://maxanstey-meridian.github.io/sluice/misc/limitations#ef-core).

A plain query depends on every row of each type it reads: renaming any user evicts every
dashboard that read users. Sluice logs each cached query that does this the first time it runs,
at `Information`. When that's too broad, narrow a query to the rows it reads:

```csharp
// One row, by key.
var user = await read.Entity(computeDb.Users, userId).SingleAsync(ct);

// The rows under one parent.
var orders = await read.Children(computeDb.Orders, o => o.UserId, userId).ToListAsync(ct);

// One row, by a column with a unique index, such as a slug or an email.
var page = await read.Entity(computeDb.Pages, p => p.Slug, slug).SingleOrDefaultAsync(ct);
```

`read.Entity` depends on one row, so renaming Bob leaves Alice's dashboard cached.
`read.Children` depends on the rows under one parent. Both return a query you can filter and
project. Keys are your model's own types: the playground maps `UserId` with a value converter,
so `read.Entity(computeDb.Users, userId)` takes a `UserId`. A compute can also leave its value
uncached with `read.DoNotCache()`, for a miss on a key from public input.

### Writing

EF writes need no Sluice code:

```csharp
var flag = await db.Flags.SingleAsync(flag => flag.Id == FlagId.DarkMode, ct);
flag.Enabled = !flag.Enabled;

// Once this commits, every dashboard that read the flag is evicted. No Sluice code needed.
await db.SaveChangesAsync(ct);
```

Once a save commits, Sluice clears the cached reads of every row it inserted, updated or
deleted, and of the lists those rows were in or moved between. `ExecuteUpdate` and
`ExecuteDelete` work too: Sluice can't tell which rows they changed, so once they commit, every
cached read of the types in the table they wrote is evicted. Name the rows with `.ForKey(id)` or
`.ForKeys(ids)` just before the call, and only those rows' readers are evicted, plus the type's
`Children` lists and, for an update, readers by any unique column the statement names; see
[Bulk writes by key](https://maxanstey-meridian.github.io/sluice/guides/ef-core#bulk-writes-by-key).
One more case is broad: when a delete makes the database delete rows EF never loaded, or set
their foreign key to null (`ON DELETE CASCADE` or `SET NULL`), Sluice clears every cached read
of those types.
[Database cascades](https://maxanstey-meridian.github.io/sluice/guides/ef-core#database-cascades)
has the details.

Sluice can't see writes that don't go through a `UseSluice` context: another service, a cron
job, SQL run by hand, a trigger, a context without `UseSluice`, or `ExecuteSql`. After one of
those, invalidate by hand, or the old value stays cached until it expires. Keep durations short
for data written outside your app. For an EF set it's one line:
`await cache.InvalidateAsync(db.Users.AllDependencies());`.

### What a compute can't read

Inside a compute, a few things throw, because Sluice couldn't tell what they read: raw SQL
(`FromSql`, `SqlQuery`), views, keyless types and database functions of your own. Read those in
the fetch of `read.From(dependency, fetch)`, with a `Source` you declare and invalidate
yourself, or with the EF sets' dependencies (`[.. db.Orders.AllDependencies()]`), so EF writes
to those types evict it with no code of yours. A query on any context other than `read.Db`'s
throws too: that context may hold your request's unsaved edits or uncommitted rows, which
mustn't be cached for everyone. So does a query filter that reads state that can change, such
as a static tenant accessor, or the context's own tenant id unless the compute set it from the
key with `read.Db<AppDb>(db => db.TenantId = tenantId)`. A filter reading a scoped tenant
service runs when the context property it reads is marked `[SluiceContextState]` and the compute
sets the service with `read.Db<AppDb>((db, services) => …)`.

A cached read inside your own transaction, after saves you haven't committed, throws when it
depends on them, rather than hand you data older than your own write. Sluice sees a
`TransactionScope` by itself; for EF transactions, add `using var _ = SluiceFlow.Begin();` once
per entry point, such as in middleware. See
[Reading your own uncommitted writes](https://maxanstey-meridian.github.io/sluice/guides/strict-reads#reading-your-own-uncommitted-writes).

One thing doesn't throw, and isn't tracked either: raw ADO.NET or Dapper on `read.Db`'s
connection (`computeDb.Database.GetDbConnection()`) outside a `read.From` fetch. Sluice only
sees EF commands, so such a read records nothing. Run it in a fetch as above.
[What a compute can't read](https://maxanstey-meridian.github.io/sluice/guides/strict-reads)
has the full list, and what else Sluice can't check.

## Good to know

- **Cached values are shared.** Every caller gets the same instance, so changing a cached object
  changes it for everyone. Cache records or projections, or turn on FusionCache's
  [auto-clone](https://maxanstey-meridian.github.io/sluice/guides/configuration#shared-instances-and-auto-clone).
- **Interface value types need the lambda's return type.** For a `Query<int, IReadOnlyList<Item>>`,
  write `async Task<IReadOnlyList<Item>> (read, ct) => …`, or C# can't infer the types from
  `ToListAsync`.
- **FusionCache logs every call at `Information`.** Set `"ZiggyCreatures": "Warning"` under
  `Logging:LogLevel` to quieten it.

## Try it

```bash
dotnet run --project examples/playground
```

Open `http://localhost:5319`. Read both dashboards, then toggle dark mode or change Alice's
greeting, and watch which dashboard recomputes. It runs on SQLite, so there's nothing else to
start.

## Also in the box

- [Typed declarations](https://maxanstey-meridian.github.io/sluice/guides/sources-and-queries):
  which key types work, typed ids, keys with several parts or a tenant, and how long each query
  is cached
- [Invalidating](https://maxanstey-meridian.github.io/sluice/guides/invalidation): several
  things at once or a whole source, fallback values that still get cleared, and clearing
  everything
- [EF Core](https://maxanstey-meridian.github.io/sluice/guides/ef-core): what each query records,
  foreign keys, database cascades, owned and inherited types, pooled contexts, and bulk updates,
  by key or not
- [What a compute can't read](https://maxanstey-meridian.github.io/sluice/guides/strict-reads):
  the full list, how to read views and raw SQL anyway, and what Sluice can't check
- [Transactions](https://maxanstey-meridian.github.io/sluice/guides/transactions): EF
  transactions, `TransactionScope` and retries all clear the cache after the commit, not before
- [Several servers](https://maxanstey-meridian.github.io/sluice/guides/configuration): sharing
  the cache through Redis, and a startup check for settings that would let cleared values come
  back

## How it's tested

- The EF Core tests run on a real PostgreSQL in Docker, not an in-memory fake.
- A random workload makes saves, moves, deletes, deletes of rows it never loaded, bulk updates
  and deletes (by key or not), batches and transactions across cascades, set-null keys, inheritance, composite
  keys and a many-to-many join. After every write, it checks each cached read, through the
  helpers and through plain queries that follow navigations, joins and `Include`, against the
  same read run fresh. A concurrent version runs four writers and four readers at once, and
  checks once they've finished. `SLUICE_RANDOM_SEEDS=50 dotnet test` runs more of both.
- Benchmarks against FusionCache on its own are in
  [Performance](https://maxanstey-meridian.github.io/sluice/misc/performance). In short: a hit
  costs about 0.2 µs per dependency, so keep each value's dependencies to tens rather than
  thousands.

## Documentation

[Getting Started](https://maxanstey-meridian.github.io/sluice/getting-started) ·
[Sources and Queries](https://maxanstey-meridian.github.io/sluice/guides/sources-and-queries) ·
[EF Core](https://maxanstey-meridian.github.io/sluice/guides/ef-core) ·
[Configuration](https://maxanstey-meridian.github.io/sluice/guides/configuration) ·
[How It Works](https://maxanstey-meridian.github.io/sluice/misc/how-it-works) ·
[Performance](https://maxanstey-meridian.github.io/sluice/misc/performance) ·
[Limitations](https://maxanstey-meridian.github.io/sluice/misc/limitations)
(what Sluice deliberately doesn't do)

Contributors: each package's rules, with the tests that prove them, are in
[`src/Sluice/INVARIANTS.md`](https://github.com/maxanstey-meridian/sluice/blob/main/src/Sluice/INVARIANTS.md) and
[`src/Sluice.EntityFrameworkCore/INVARIANTS.md`](https://github.com/maxanstey-meridian/sluice/blob/main/src/Sluice.EntityFrameworkCore/INVARIANTS.md).

## License

[MIT](https://github.com/maxanstey-meridian/sluice/blob/main/LICENSE)
