# Getting Started

This page builds the [playground](https://github.com/maxanstey-meridian/sluice/tree/main/examples/playground)
from nothing: a cached dashboard for two users, read partly through EF Core and partly from a
service that isn't EF, with each write evicting only the dashboards that read what it changed.

## Install

```bash
dotnet add package Meridian.Sluice
dotnet add package Meridian.Sluice.EntityFrameworkCore
```

Sluice targets .NET 10 and runs on FusionCache 2.9, which it brings in. The EF Core package
needs EF Core 10.0.12 or later on a relational provider. It's tested on PostgreSQL, and the
playground uses SQLite.

Leave out `Meridian.Sluice.EntityFrameworkCore` if you don't use EF Core. Everything on this
page except the `AppDb` model, the `AddDbContext` and `UseSluice` registration, the
`read.Entity` lines and the EF write works with the core package alone.

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
public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Flag> Flags => Set<Flag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Id).HasConversion(id => id.Value, value => new UserId(value));
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
            flag.Property(f => f.Id).HasConversion(id => id.Value, value => new FlagId(value));
            flag.HasData(new Flag { Id = FlagId.DarkMode });
        });
    }
}
```

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
builder.Services.AddDbContext<AppDb>(
    (services, options) => options.UseSqlite(connectionString).UseSluice(services)
);
```

`AddSluice` registers `SluiceCache` over a FusionCache of its own. Pass FusionCache's builder
to it for durations, a distributed cache or a backplane: see
[Configuration](/guides/configuration).

`UseSluice` does two things. It invalidates what each `SaveChanges` wrote once the write has
committed, and it makes EF reads inside a compute [strict](/guides/strict-reads). It needs
`AddSluice` and logging registered; ASP.NET Core and the Generic Host register logging for
you.

Cached values are shared: every caller gets the same instance, entities from the EF helpers
included. Don't change one in place, or turn on FusionCache's auto-clone; see
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

- `Source<UserId>` is a family of dependencies keyed by a user id. `Greeting.For(aliceId)` is
  the dependency on Alice's greeting; `Greeting.All` is the dependency on every greeting.
- `Query<UserId, Dashboard>` is a cached value: a user id in, a `Dashboard` out. Its entries
  live for five minutes unless something evicts them first.
- `id => id.Value` turns a `UserId` into text, for tags and entry keys. A record needs one; a
  `string` or `IFormattable` key (numbers, `Guid`, enums) doesn't.
- EF entities need no declaration. Their dependencies come from the EF model.

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

`cache` is the `SluiceCache`, `db` the `AppDb`, and `greetings` the `GreetingApi`, all injected
into the class that holds this method.

The lambda is the compute: the function that builds the value on a miss. On a hit, the cached
`Dashboard` comes back and the compute doesn't run. On a miss, the compute runs, and each
`read.*` call records a dependency:

| Read | Records |
|---|---|
| `read.Entity(db.Users, userId)` | the user row with that key |
| `read.Entity(db.Flags, FlagId.DarkMode)` | the dark-mode flag row |
| `read.From(Dashboards.Greeting.For(userId), fetch)` | that user's greeting, then runs `fetch` |

Recording happens as the compute runs, so it records what was actually read. Bob isn't an admin,
so his compute never reaches the greeting, and his dashboard doesn't depend on one.

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

Both dashboards read the flag, so both are evicted, and the next read of each recomputes.

A write Sluice can't see, like the greeting service's, says what it changed once it has
committed:

```csharp
public async Task SetGreeting(UserId userId, string text)
{
    await greetings.SetAsync(userId, text);

    // Not EF, so say what changed, after the write.
    await cache.InvalidateAsync(Dashboards.Greeting.For(userId));
}
```

Only Alice's dashboard read Alice's greeting, so Bob's stays cached.

The same goes for anything that writes without a `UseSluice` context: another service, a cron
job, SQL run by hand, a trigger, `ExecuteUpdate` or raw SQL. It must invalidate by hand, or the
old value stays cached until it expires, so keep durations short for data written outside your
app. See [Writes Sluice can't see](/guides/ef-core#writes-sluice-can-t-see).

## Check the declarations in a test

A declaration that breaks a rule (a blank name, a key type with no text form) throws when its
class is first touched. One test per declaring class, here `Dashboards`, catches that before
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
- [EF Core](/guides/ef-core): the helpers, what a save invalidates, and bypasses
- [Strict Reads](/guides/strict-reads): what's refused inside a compute, and why
- [Configuration](/guides/configuration): FusionCache options and multi-node setups
