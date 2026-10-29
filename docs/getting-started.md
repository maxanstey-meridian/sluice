# Getting Started

This page builds the
[playground](https://github.com/maxanstey-meridian/sluice/tree/main/examples/playground) from
nothing: a cached dashboard for two users, built partly from EF Core and partly from a service that
isn't EF. Each write throws away (evicts) only the dashboards that read what it changed.

## Install

```bash
dotnet add package Meridian.Sluice
dotnet add package Meridian.Sluice.EntityFrameworkCore
```

Sluice targets .NET 10 and runs on FusionCache 2.9, which it brings in. The EF Core package
needs EF Core 10.0.12 or later on a relational provider. It's tested on PostgreSQL, and the
playground uses SQLite. The two packages share a version number, and the EF Core package
depends on exactly its own version of `Meridian.Sluice`, so upgrade them together.

Leave out `Meridian.Sluice.EntityFrameworkCore` if you don't use EF Core. Everything on this
page except the `AppDb` model, the `AddDbContextFactory` and `UseSluice` registration, the
`read.Db` lines and the EF write works with the core package alone. Everything Sluice has is in
the `Sluice` namespace.

## The data

Typed ids, a role, and two EF entities, a user and a feature flag:

```csharp
public sealed record UserId(string Value);

public sealed record FlagId(string Value)
{
    public static readonly FlagId DarkMode = new("dark-mode");
}

public enum Role
{
    Member,
    Admin,
}

public sealed class User
{
    public required UserId Id { get; init; }
    public required string Name { get; set; }
    public Role Role { get; set; }
}

public sealed class Flag
{
    public required FlagId Id { get; init; }
    public bool Enabled { get; set; }
}
```

The context maps each id to its column with a value converter, so EF, and Sluice, use the typed
id as the key:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sluice;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Flag> Flags => Set<Flag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Id).HasConversion(new UserIdConverter());
            user.HasData(
                new User
                {
                    Id = new UserId("alice"),
                    Name = "Alice",
                    Role = Role.Admin,
                },
                new User
                {
                    Id = new UserId("bob"),
                    Name = "Bob",
                    Role = Role.Member,
                }
            );
        });

        modelBuilder.Entity<Flag>(flag =>
        {
            flag.Property(f => f.Id).HasConversion(new FlagIdConverter());
            flag.HasData(new Flag { Id = FlagId.DarkMode });
        });
    }
}

// A typed id over the column's own value round-trips, which Sluice needs vouched for on a key.
[SluiceRoundTrips]
public sealed class UserIdConverter()
    : ValueConverter<UserId, string>(id => id.Value, value => new UserId(value));

[SluiceRoundTrips]
public sealed class FlagIdConverter()
    : ValueConverter<FlagId, string>(id => id.Value, value => new FlagId(value));
```

Each converter on a key, foreign key or unique column is a class of its own, marked
`[SluiceRoundTrips]` to say it gives back exactly the value it loaded; Sluice refuses an unmarked
one, naming the property. Lambdas passed to `HasConversion` have no class to mark, which is why
they move into one. See [Key folding](/guides/ef-core#key-folding) for why.

And a greeting API that isn't EF. In a real app it might be an HTTP API, Dapper or a file:

```csharp
public sealed class GreetingApi
{
    private readonly ConcurrentDictionary<UserId, string> _greetings = new()
    {
        [new UserId("alice")] = "Hello, Admin!",
    };

    public async Task<string?> GetAsync(UserId userId, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20), ct); // a network round trip
        return _greetings.GetValueOrDefault(userId);
    }

    public Task SetAsync(UserId userId, string text)
    {
        _greetings[userId] = text;
        return Task.CompletedTask;
    }
}
```

## Register

```csharp
builder.Services.AddSluice();
builder.Services.AddSluiceEntityFrameworkCore();
builder.Services.AddDbContextFactory<AppDb>(
    (services, options) => options.UseSqlite(connectionString).UseSluice(services)
);
```

`AddSluice` registers `SluiceCache`, which you inject wherever you cache or invalidate. It
creates a FusionCache of its own. To configure it (durations, Redis, several servers), pass a
callback to `AddSluice`; see [Configuration](/guides/configuration).
`AddSluiceEntityFrameworkCore` registers the EF package, beside `AddSluice`; `UseSluice` throws
without it.

`AddDbContextFactory` registers `AppDb` for your own code (`AddDbContext` works too). Each
cached value's computation takes an `AppDb` of its own from a DI scope it opens for itself.

`UseSluice` does two things. Each time `SaveChanges` commits, it evicts what the save changed; an
`ExecuteUpdate` or `ExecuteDelete` evicts every read of the types it wrote, or mostly only its rows'
readers when you name them with [`ForKey`](/guides/ef-core#bulk-writes-by-key). And while a cached
value is being built, it records the entity types each query reads, and makes queries it can't track
throw (see [What a compute can't read](/guides/strict-reads)). It needs `AddSluice` and logging
registered; ASP.NET Core and the Generic Host register logging for you.

Every caller gets the same cached object, EF entities included. Don't modify one, or turn on
FusionCache's auto-clone to give each caller a copy; see
[Shared instances](/guides/configuration#shared-instances-and-auto-clone).

## Declare

```csharp
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

- `Source<UserId>` is something that can change, one per user. `Greeting.For(aliceId)` is
  Alice's greeting, and `Greeting.All` is every user's greeting. Each of these is a
  *dependency*: something a cached value can read, and a write can change.
- `Query<UserId, Dashboard>` is something cached: a user id in, a `Dashboard` out. Its entries
  live for five minutes unless a write evicts them first.
- `id => id.Value` turns a `UserId` into text, which Sluice builds its cache keys from. A record
  needs one; a `string` or `IFormattable` key (numbers, `Guid`, enums) doesn't.
- EF entities need no declaration. Sluice works out their dependencies from the EF model.
- A value with no key, such as a list of every user, is a `Query<TValue>`: `new("users")`.

Both are declared once, as `static readonly` fields, and shared by every reader and writer.
[Sources and Queries](/guides/sources-and-queries) covers key types, names and durations.

## Cache a read

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

`cache` is the `SluiceCache` and `greetings` the `GreetingApi`, both injected into the class that
holds this method.

The lambda is the *compute*: the function that builds the value when it isn't cached. If the
dashboard is cached, it comes back and the compute doesn't run. If it isn't, the compute runs,
and records what it reads:

| Read | Records |
|---|---|
| `read.Db<AppDb>()` | nothing yet: it's the compute's own `AppDb`, made in the compute's own DI scope and disposed when the compute ends |
| `computeDb.Users.SingleAsync(…)` | every `User` row |
| `computeDb.Flags.Where(…)…` | every `Flag` row |
| `read.From(Dashboards.Greeting.For(userId), fetch)` | that user's greeting, then runs `fetch` |

Recording happens as the compute runs, so it records what was actually read. Bob isn't an admin,
so his compute never reaches the greeting, and his dashboard doesn't depend on one.

A plain query depends on every row of its type: renaming any user evicts both dashboards. To
depend on one row, start the query from `read.Entity(computeDb.Users, userId)` instead; see
[Narrowing to rows](/guides/ef-core#narrowing-to-rows).

Query the context `read.Db` gives you, not one you injected: the compute's own context holds
nothing from your request, so it can't cache your unsaved edits for everyone. A query on any
other context throws. The sample names it `computeDb` so it can't be mistaken for the injected
`db` the writes below use.

A compute must record at least one dependency, or the call throws and nothing is cached. The
`ct` the compute gets is the caller's token.

## Write

An EF write needs nothing:

```csharp
public async Task<bool> ToggleDarkMode(CancellationToken ct)
{
    var flag = await db.Flags.SingleAsync(flag => flag.Id == FlagId.DarkMode, ct);
    flag.Enabled = !flag.Enabled;

    // Once this commits, every dashboard that read the flag is evicted. No Sluice code needed.
    await db.SaveChangesAsync(ct);
    return flag.Enabled;
}
```

Both dashboards read the `Flag` type, so both are evicted, and the next read of each recomputes.

Sluice can't see the greeting service's writes, so after one, tell it what changed:

```csharp
public async Task SetGreeting(UserId userId, string text)
{
    await greetings.SetAsync(userId, text);

    // Not EF, so say what changed, after the write.
    await cache.InvalidateAsync(Dashboards.Greeting.For(userId));
}
```

Only Alice's dashboard read Alice's greeting, so Bob's stays cached. Keep the invalidation
inside the method that writes, as here, and have every caller go through it: then no write can
forget it. See
[Keep the invalidation with the write](/guides/invalidation#keep-the-invalidation-with-the-write).

The same goes for anything that writes without a `UseSluice` context: another service, a cron
job, SQL run by hand, a trigger, or `ExecuteSql`. After one of those, invalidate by hand, or the
old value stays cached until it expires. Keep durations short for data written outside your
app. See [Writes Sluice can't see](/guides/ef-core#writes-sluice-can-t-see).

## Check the declarations in a test

A broken declaration (a blank name, a key type Sluice can't turn into text) throws the first
time its class is used. One test per declaring class, here `Dashboards`, finds that before
production does; see
[When declarations are checked](/guides/sources-and-queries#when-declarations-are-checked).

## Run the playground

```bash
dotnet run --project examples/playground
```

Open `http://localhost:5319`. Read both dashboards, read them again (both come from the cache),
then toggle dark mode or set Alice's greeting and read again. Each card shows how many times
that dashboard has been computed. The database is a SQLite file, recreated on each run.

## Next

- [Sources and Queries](/guides/sources-and-queries): key types, typed ids, names, durations
- [Reading and Invalidating](/guides/invalidation): the generic path in full
- [EF Core](/guides/ef-core): what a query records, the helpers, what a write invalidates
- [What a compute can't read](/guides/strict-reads): what throws inside a compute, and why
- [Configuration](/guides/configuration): FusionCache options and multi-node setups
