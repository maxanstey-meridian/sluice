using Microsoft.EntityFrameworkCore;

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
