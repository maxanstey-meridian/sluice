using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Testcontainers.PostgreSql;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>One PostgreSQL container for the whole run, with the <see cref="ShopDb"/> schema created once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _server = new PostgreSqlBuilder(
        "postgres:17-alpine"
    ).Build();

    public string ConnectionString => _server.GetConnectionString();

    public ShopDb CreateDb() =>
        new(new DbContextOptionsBuilder<ShopDb>().UseNpgsql(ConnectionString).Options);

    /// <summary>
    /// A context whose tracked writes invalidate <paramref name="node"/>'s cache. <paramref name="interceptors"/>
    /// run before Sluice's.
    /// </summary>
    public ShopDb CreateDb(
        SluiceNode node,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null,
        params IInterceptor[] interceptors
    ) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql(ConnectionString, npgsql)
                .AddInterceptors(interceptors)
                .UseSluice(node.Services)
                .Options
        );

    /// <summary>
    /// A context whose tracked writes invalidate <paramref name="node"/>'s cache, with <paramref name="interceptors"/>
    /// added after Sluice, so they run after it.
    /// </summary>
    public ShopDb CreateDbWithInterceptorsAfterSluice(
        SluiceNode node,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql,
        params IInterceptor[] interceptors
    ) =>
        new(
            new DbContextOptionsBuilder<ShopDb>()
                .UseNpgsql(ConnectionString, npgsql)
                .UseSluice(node.Services)
                .AddInterceptors(interceptors)
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
    }

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
