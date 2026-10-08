using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>One PostgreSQL container for the whole run, with the <see cref="ShopDb"/> schema created once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _server = new PostgreSqlBuilder(
        "postgres:17-alpine"
    ).Build();

    public ShopDb CreateDb() =>
        new(new DbContextOptionsBuilder<ShopDb>().UseNpgsql(_server.GetConnectionString()).Options);

    /// <summary>A context whose connection string sets <paramref name="enlist"/>.</summary>
    public ShopDb CreateDb(bool enlist) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql($"{_server.GetConnectionString()};Enlist={enlist}")
                .Options
        );

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
