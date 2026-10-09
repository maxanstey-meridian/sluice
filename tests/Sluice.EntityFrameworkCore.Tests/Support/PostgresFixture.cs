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

    /// <summary>The started fixture, which a node's own <see cref="ShopDb"/> factory connects to.</summary>
    public static PostgresFixture Running =>
        _running
        ?? throw new InvalidOperationException("No test in the Postgres collection is running.");

    private static PostgresFixture? _running;

    public string ConnectionString => _server.GetConnectionString();

    /// <summary>
    /// A context on <paramref name="connection"/>, or on a connection string that sets <paramref name="enlist"/>. With
    /// a <paramref name="node"/>, its tracked writes invalidate the node's cache, and <paramref name="before"/> is
    /// added before Sluice and <paramref name="after"/> after it, so they run in that order.
    /// </summary>
    public ShopDb CreateDb(
        SluiceNode? node = null,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null,
        IInterceptor[]? before = null,
        IInterceptor[]? after = null,
        DbConnection? connection = null,
        bool enlist = true
    )
    {
        var options = new DbContextOptionsBuilder<ShopDb>();
        if (connection is null)
        {
            options.UseNpgsql(
                enlist ? ConnectionString : $"{ConnectionString};Enlist=false",
                npgsql
            );
        }
        else
        {
            options.UseNpgsql(connection, npgsql);
        }

        options.AddInterceptors(before ?? []);
        if (node is not null)
        {
            options.UseSluice(node.Services);
        }

        return new(options.AddInterceptors(after ?? []).Options);
    }

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        _running = this;
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
