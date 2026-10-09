using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Testcontainers.PostgreSql;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// One PostgreSQL container for the whole run, with the <see cref="ShopDb"/> schema created once, and the view its
/// keyless <see cref="CustomerTotal"/> reads.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _server = new PostgreSqlBuilder(
        "postgres:17-alpine"
    ).Build();

    public string ConnectionString => _server.GetConnectionString();

    public ShopDb CreateDb() =>
        new(new DbContextOptionsBuilder<ShopDb>().UseNpgsql(ConnectionString).Options);

    /// <summary>
    /// A context whose tracked writes invalidate <paramref name="node"/>'s cache, with <paramref name="before"/> added
    /// before Sluice and <paramref name="after"/> after it, so they run in that order.
    /// </summary>
    public ShopDb CreateDb(
        SluiceNode node,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null,
        IInterceptor[]? before = null,
        IInterceptor[]? after = null
    ) => CreateDb(node.Services, npgsql, before, after);

    /// <summary>
    /// A context whose tracked writes invalidate the cache <paramref name="services"/> holds, with
    /// <paramref name="before"/> added before Sluice and <paramref name="after"/> after it, so they run in that order.
    /// </summary>
    public ShopDb CreateDb(
        IServiceProvider services,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null,
        IInterceptor[]? before = null,
        IInterceptor[]? after = null
    ) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql(ConnectionString, npgsql)
                .AddInterceptors(before ?? [])
                .UseSluice(services)
                .AddInterceptors(after ?? [])
                .Options
        );

    /// <summary>A context on <paramref name="connection"/> whose tracked writes invalidate <paramref name="node"/>'s cache.</summary>
    public ShopDb CreateDb(SluiceNode node, DbConnection connection) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql(connection)
                .UseSluice(node.Services)
                .Options
        );

    /// <summary>
    /// A context whose tracked writes invalidate <paramref name="node"/>'s cache, and whose connection string sets
    /// <paramref name="enlist"/>.
    /// </summary>
    public ShopDb CreateDb(SluiceNode node, bool enlist) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql($"{ConnectionString};Enlist={enlist}")
                .UseSluice(node.Services)
                .Options
        );

    /// <summary>A context whose connection string sets <paramref name="enlist"/>.</summary>
    public ShopDb CreateDb(bool enlist) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql($"{ConnectionString};Enlist={enlist}")
                .Options
        );

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            create view customer_totals as
            select "CustomerId", count(*)::int as "Orders" from "Orders" group by "CustomerId"
            """
        );
    }

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
