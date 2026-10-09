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
part. With EF Core, saving your changes clears the right entries by itself. With any other data
source, you tell Sluice what changed after you write it.

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

## Three steps in

The code below is the
[playground](https://github.com/maxanstey-meridian/sluice/tree/main/examples/playground)'s: a
dashboard for Alice, an admin, and Bob, a member. Both read their user row and a dark-mode flag. Only Alice reads a greeting,
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

A `Source` names something that can change. Reads record it and writes clear it, and both use
this one declaration, so they can't disagree on a name. A `Query` names something cached, with
its key and value types, so the compiler checks every call. Sluice needs each key as text, and
`UserId` isn't a string, so each declaration says how to turn it into one (`id => id.Value`).
Strings, numbers, `Guid`s and enums need nothing.

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

The lambda builds the dashboard when it isn't cached; Sluice calls it the compute. Each `read`
call in it records what it read. `read.From(dependency, fetch)` works with any data source: it
records the dependency and runs the fetch in one call, so you can't record one thing and fetch
another. `read.Entity` is the EF Core version, covered below.

After both dashboards are cached, Alice's depends on her user row, the flag and her greeting.
Bob's depends on his row and the flag: he isn't an admin, so his compute never read a greeting.

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

## With EF Core

```csharp
builder.Services.AddDbContext<AppDb>(
    (services, options) => options.UseSqlite(connectionString).UseSluice(services)
);
```

`UseSluice` connects the context to Sluice. Inside a compute, three helpers read EF data:
`read.Entity` (one row by its key), `read.Children` (the rows under one parent) and `read.All`
(every row). Each records what it reads and returns a query you can filter and project. Sluice
works out the dependency from your EF model, so EF entities need no `Source`. Keys are your
model's own types: the playground maps `UserId` with a value converter, so
`read.Entity(db.Users, userId)` takes a `UserId`.

EF writes need no Sluice code:

```csharp
var flag = await db.Flags.SingleAsync(flag => flag.Id == FlagId.DarkMode, ct);
flag.Enabled = !flag.Enabled;

// Once this commits, every dashboard that read the flag is evicted. No Sluice code needed.
await db.SaveChangesAsync(ct);
```

Saves through EF's change tracker just work. Once a save commits, Sluice clears the cached
reads of every row it inserted, updated or deleted, and of the lists those rows were in or
moved between. One case is broader: when a delete makes the database delete rows EF never
loaded, or set their foreign key to null (`ON DELETE CASCADE` or `SET NULL`), Sluice can't tell
which rows those were. It clears every cached read of those types instead.
[Database cascades](https://maxanstey-meridian.github.io/sluice/guides/ef-core#database-cascades)
has the details.

Sluice can't see writes that don't go through a `UseSluice` context: another service, a cron
job, SQL run by hand, a trigger, a second context without `UseSluice`, `ExecuteUpdate` or raw
SQL. After one of those, invalidate by hand, or the old value stays cached until it expires.
Keep durations short for data written outside your app. For an EF set it's one line:
`await cache.InvalidateAsync(db.Users.AllDependencies());`.

Inside a compute, an EF query that skips the helpers throws. Sluice wouldn't know what it read,
so no write would ever clear the value it built.

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
- [EF Core writes](https://maxanstey-meridian.github.io/sluice/guides/ef-core): foreign keys,
  database cascades, owned and inherited types, pooled contexts, and bulk updates
- [Strict reads](https://maxanstey-meridian.github.io/sluice/guides/strict-reads): queries that
  would cache data Sluice can't track throw, and how to read views and raw SQL anyway
- [Transactions](https://maxanstey-meridian.github.io/sluice/guides/transactions): EF
  transactions, `TransactionScope` and retries all clear the cache after the commit, not before
- [Several servers](https://maxanstey-meridian.github.io/sluice/guides/configuration): sharing
  the cache through Redis, and a startup check for settings that would let cleared values come
  back

## Documentation

[Getting Started](https://maxanstey-meridian.github.io/sluice/getting-started) ·
[Sources and Queries](https://maxanstey-meridian.github.io/sluice/guides/sources-and-queries) ·
[EF Core](https://maxanstey-meridian.github.io/sluice/guides/ef-core) ·
[Configuration](https://maxanstey-meridian.github.io/sluice/guides/configuration) ·
[How It Works](https://maxanstey-meridian.github.io/sluice/misc/how-it-works) ·
[Limitations](https://maxanstey-meridian.github.io/sluice/misc/limitations)
(what Sluice deliberately doesn't do)

Contributors: each package's rules, with the tests that prove them, are in
[`src/Sluice/INVARIANTS.md`](https://github.com/maxanstey-meridian/sluice/blob/main/src/Sluice/INVARIANTS.md) and
[`src/Sluice.EntityFrameworkCore/INVARIANTS.md`](https://github.com/maxanstey-meridian/sluice/blob/main/src/Sluice.EntityFrameworkCore/INVARIANTS.md).

## License

[MIT](https://github.com/maxanstey-meridian/sluice/blob/main/LICENSE)
