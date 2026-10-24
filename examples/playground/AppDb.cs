using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sluice;

namespace Playground;

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
