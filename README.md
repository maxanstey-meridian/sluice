<p align="center">
  <h1 align="center">Sluice</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Meridian.Sluice"><img src="https://img.shields.io/nuget/v/Meridian.Sluice?label=Meridian.Sluice" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/Meridian.Sluice.EntityFrameworkCore"><img src="https://img.shields.io/nuget/v/Meridian.Sluice.EntityFrameworkCore?label=Meridian.Sluice.EntityFrameworkCore" alt="NuGet" /></a>
    <img src="https://img.shields.io/badge/license-MIT-blue" alt="License" />
  </p>
</p>

**Your reads are your invalidation rules.** Sluice caches computed values and records, while
each one is computed, exactly what it read. When something it read changes, that value is
evicted. Values that didn't read it stay cached. You never write a list of cache keys to
evict.

[FusionCache](https://github.com/ZiggyCreatures/FusionCache) does the caching: memory and
distributed levels, the backplane, stampede protection and serialisation. Sluice is the thin
layer on top. You pass a compute, the function that builds a value on a miss; Sluice turns what
it read into FusionCache tags, and removes by tag once a write has committed. Any data source works. With EF Core, the dependencies come from the
model, and writes need no Sluice code at all.

## The rules

```text
Declare each dependency once.
Computes record what they read.
Writes invalidate what they changed.
Invalidation waits for the commit.
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

## Three steps in

The code below is the [playground](examples/playground)'s: a dashboard for Alice, an admin,
and Bob, a member. Both read their user row and a dark-mode flag. Only Alice reads a greeting,
which comes from a service that isn't EF.

### 1. Declare what can change, and what you cache

```csharp
builder.Services.AddSluice();
```

```csharp
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

A `Source` is declared once and used on both sides: computes record it, writes invalidate it.
A `Query` binds a name, a key type and a value type, so the compiler checks every call.
`UserId` isn't a string, so each declaration says once how to turn it into text. Keys that are
`string` or `IFormattable` (numbers, `Guid`, enums) need no `format`.

### 2. Cache a read

```csharp
public ValueTask<Dashboard> GetDashboard(UserId userId, CancellationToken ct) =>
    cache.GetOrSetAsync(
        Dashboards.ForUser,
        userId,
        async (read, ct) =>
        {
            // EF Core: each read records the row it reads.
            var user = await read.Entity(db.Users, userId).SingleAsync(ct);
            var darkMode = await read.Entity(db.Flags, FlagId.DarkMode)
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

On a miss, Sluice runs the compute and records every dependency it touches through `read`.
`read.From(dependency, fetch)` works for any data source: it records the dependency, then runs
the fetch, so the two can't drift apart. `read.Entity` is the EF Core form, covered below.
Alice's dashboard now depends on her user row, the flag and her greeting; Bob's on his row and
the flag.

### 3. Invalidate once the write has committed

```csharp
public async Task SetGreeting(UserId userId, string text)
{
    await greetings.SetAsync(userId, text);

    // Not EF, so say what changed, after the write.
    await cache.InvalidateAsync(Dashboards.Greeting.For(userId));
}
```

Only Alice's dashboard read Alice's greeting, so only hers is evicted. Bob's stays cached.
Invalidate after the write commits, never before: in between, another request could cache the
old value again.

## With EF Core

```csharp
builder.Services.AddDbContext<AppDb>(
    (services, options) => options.UseSqlite(connectionString).UseSluice(services)
);
```

`UseSluice` connects the context to Sluice. Inside a compute, `read.Entity`, `read.Children`
and `read.All` record dependencies taken from the EF model and return no-tracking queries you
can filter and project. Keys are the model's own types: the playground maps `UserId` with a
value converter, so `read.Entity(db.Users, userId)` takes a `UserId`. EF writes need nothing:

```csharp
var flag = await db.Flags.SingleAsync(flag => flag.Id == FlagId.DarkMode, ct);
flag.Enabled = !flag.Enabled;

// Once this commits, every dashboard that read the flag is evicted. No Sluice code needed.
await db.SaveChangesAsync(ct);
```

Tracked saves just work. Inserts, updates and deletes evict exactly the rows they wrote and the
foreign keys those rows had before and after, once the transaction commits. When a delete lets
the database remove or null rows EF never loaded (an `ON DELETE CASCADE` or `SET NULL` in your
model), Sluice can't see which rows those were, so it evicts every cached read of the affected
types that could include them instead. [Database cascades](https://maxanstey-meridian.github.io/sluice/guides/ef-core#database-cascades)
has the details.

Anything that writes without a `UseSluice` context must invalidate by hand: another service, a
cron job, SQL run by hand, a trigger, a second context without `UseSluice`, `ExecuteUpdate` or
raw SQL. Otherwise the old value stays cached until it expires, so keep durations short for
data written outside your app. For an EF set it's one line:
`await cache.InvalidateAsync(db.Users.AllDependencies());`.

Reads inside a compute are strict. An EF query that doesn't go through a helper throws,
rather than caching a value nothing will ever invalidate.

## Try it

```bash
dotnet run --project examples/playground
```

Open `http://localhost:5319`. Read both dashboards, then toggle dark mode or change Alice's
greeting, and watch which dashboard recomputes. It runs on SQLite, so there's nothing else to
start.

## Also in the box

- [Typed declarations](https://maxanstey-meridian.github.io/sluice/guides/sources-and-queries):
  key rules, typed ids, multi-part and per-tenant keys, and durations per query
- [Batched and whole-source invalidation](https://maxanstey-meridian.github.io/sluice/guides/invalidation),
  fallbacks that are still evicted when a fetch fails, and `ClearAsync`
- [EF Core writes](https://maxanstey-meridian.github.io/sluice/guides/ef-core): foreign keys,
  database cascades, owned and inherited types, pooled contexts, and bulk-statement bypasses
- [Strict reads](https://maxanstey-meridian.github.io/sluice/guides/strict-reads): forgotten
  reads, lazy loads and joins throw inside a compute, with a declared route for views and raw SQL
- [Transactions](https://maxanstey-meridian.github.io/sluice/guides/transactions): EF
  transactions, `TransactionScope` and retrying execution strategies all invalidate after commit
- [Multi-node](https://maxanstey-meridian.github.io/sluice/guides/configuration): FusionCache's
  distributed cache and backplane, with a startup check that entries can't outlive their tags

## Documentation

[Getting Started](https://maxanstey-meridian.github.io/sluice/getting-started) ·
[Sources and Queries](https://maxanstey-meridian.github.io/sluice/guides/sources-and-queries) ·
[EF Core](https://maxanstey-meridian.github.io/sluice/guides/ef-core) ·
[Configuration](https://maxanstey-meridian.github.io/sluice/guides/configuration) ·
[How It Works](https://maxanstey-meridian.github.io/sluice/misc/how-it-works) ·
[Limitations](https://maxanstey-meridian.github.io/sluice/misc/limitations)
(what Sluice deliberately doesn't do)

Contributors: each package's rules, with the tests that prove them, are in
[`src/Sluice/INVARIANTS.md`](src/Sluice/INVARIANTS.md) and
[`src/Sluice.EntityFrameworkCore/INVARIANTS.md`](src/Sluice.EntityFrameworkCore/INVARIANTS.md).

## License

[MIT](LICENSE)
