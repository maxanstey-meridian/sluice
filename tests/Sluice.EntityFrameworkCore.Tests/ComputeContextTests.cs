using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>The context <c>read.Db</c> gives a compute, and how <c>UseSluice</c> is set up.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ComputeContextTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_compute_gets_one_context_that_does_not_track_and_is_disposed_when_it_ends()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        await db.AddCustomers();
        ShopDb? first = null;
        ShopDb? second = null;
        var tracked = -1;

        await CachedRead.Of(
            node.Cache,
            async (read, ct) =>
            {
                first = read.Db<ShopDb>();
                second = read.Db<ShopDb>();
                await first.Customers.ToListAsync(ct);
                tracked = first.ChangeTracker.Entries().Count();
            }
        );

        second.Should().BeSameAs(first);
        tracked.Should().Be(0);
        first!
            .Invoking(context => context.Customers.Count())
            .Should()
            .Throw<ObjectDisposedException>();
    }

    // As the compute disposes what it holds (SLUICE-CORE-015): a context whose dispose throws doesn't stop the rest,
    // the probe scope included, and the caller gets what it threw.
    [Fact]
    public async Task When_one_context_s_dispose_throws_the_compute_s_other_contexts_and_scopes_are_still_disposed()
    {
        var made = new List<ShopDb>();
        await using var node = new SluiceNode(services: services =>
        {
            void Pooled<TContext>()
                where TContext : DbContext =>
                services.AddDbContextPool<TContext>(
                    (provider, options) =>
                        options
                            .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                            .UseSluice(provider)
                );

            Pooled<FailingDisposeHelpdeskDb>();
            Pooled<PooledHelpdeskDb>();
            services.AddScoped(provider =>
            {
                var db = new ShopDb(
                    new DbContextOptionsBuilder<ShopDb>()
                        .UseNpgsql(postgres.ConnectionString)
                        .UseSluice(provider)
                        .Options
                );
                made.Add(db);
                return db;
            });
        });
        PooledHelpdeskDb? builtAfter = null;
        var computes = 0;

        var read = () =>
            CachedRead.Of(
                node.Cache,
                async (r, ct) =>
                {
                    computes++;
                    r.Db<FailingDisposeHelpdeskDb>();
                    builtAfter = r.Db<PooledHelpdeskDb>();
                    await r.Db<ShopDb>().Customers.CountAsync(ct);
                }
            );

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(FailingDisposeHelpdeskDb.Failure);
        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(FailingDisposeHelpdeskDb.Failure);
        computes.Should().Be(2, "nothing was cached");
        builtAfter!
            .Invoking(context => context.Model)
            .Should()
            .Throw<ObjectDisposedException>(
                "a context built after the one that threw is disposed too"
            );
        made.Should()
            .HaveCount(
                3,
                "each compute's scope made one, and the first compute's probe scope another"
            )
            .And.AllSatisfy(db =>
                db.Invoking(context => context.Model).Should().Throw<ObjectDisposedException>()
            );
    }

    [Fact]
    public async Task UseSluice_twice_on_one_context_adds_its_interceptors_once()
    {
        await using var node = new SluiceNode();
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseNpgsql(postgres.ConnectionString)
            .UseSluice(node.Services)
            .UseSluice(node.Services)
            .Options;
        await using var db = new ShopDb(options);
        var (ada, _) = await db.AddCustomers();
        var adaRead = await CachedRead.Customer(node.Cache, ada);

        options.FindExtension<CoreOptionsExtension>()!.Interceptors.Should().HaveCount(2);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Rename(ada, "Ada Lovelace");
            (await adaRead.WasEvicted()).Should().BeFalse("the save hasn't committed");
            await transaction.CommitAsync();
        }

        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    // A context every scope gets as one instance would be shared by every compute and request, and a compute's
    // configure would change it under them, so it's refused before the configure runs.
    [Theory]
    [InlineData("a singleton")]
    [InlineData("a scoped factory handing out one instance")]
    public async Task A_context_every_scope_shares_throws_before_its_first_query_and_caches_nothing(
        string registration
    )
    {
        ShopDb? shared = null;
        await using var node = new SluiceNode(services: services =>
        {
            if (registration == "a singleton")
            {
                services.AddDbContext<ShopDb>(
                    (provider, options) =>
                        options.UseNpgsql(postgres.ConnectionString).UseSluice(provider),
                    ServiceLifetime.Singleton,
                    ServiceLifetime.Singleton
                );
            }
            else
            {
                services.AddScoped(provider =>
                    shared ??= new ShopDb(
                        new DbContextOptionsBuilder<ShopDb>()
                            .UseNpgsql(postgres.ConnectionString)
                            .UseSluice(provider)
                            .Options
                    )
                );
            }
        });
        var computes = 0;
        var configured = 0;

        var read = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) =>
                {
                    computes++;
                    return r.Db<ShopDb>(_ => configured++).Customers.CountAsync(ct);
                }
            );

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "read.Db<ShopDb>() got the same ShopDb from two DI scopes, so it's registered as a singleton*"
                    + "Register it scoped (AddDbContext's default) or transient*"
            );
        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*two DI scopes*");
        computes.Should().Be(2, "nothing was cached");
        configured.Should().Be(0);
    }

    // EF runs a query expression interceptor from the options in their order, OnConfiguring's last, so one after
    // UseSluice would rewrite a query after Sluice worked out what it reads.
    [Theory]
    [InlineData("after UseSluice")]
    [InlineData("in OnConfiguring")]
    [InlineData("before UseSluice")]
    public async Task A_query_expression_interceptor_after_UseSluice_is_refused_and_one_before_it_is_not(
        string where
    )
    {
        var connection = postgres.ConnectionString;
        DbContextOptionsBuilder<ShopDb> Shop(IServiceProvider provider) =>
            where == "before UseSluice"
                ? new DbContextOptionsBuilder<ShopDb>()
                    .UseNpgsql(connection)
                    .AddInterceptors(QueryRewriter.Instance)
                    .UseSluice(provider)
                : new DbContextOptionsBuilder<ShopDb>()
                    .UseNpgsql(connection)
                    .UseSluice(provider)
                    .AddInterceptors(QueryRewriter.Instance);
        await using var node = new SluiceNode(services: services =>
        {
            services.AddScoped(provider => new ShopDb(Shop(provider).Options));
            services.AddScoped(provider => new ConfiguringHelpdeskDb(
                new DbContextOptionsBuilder<ConfiguringHelpdeskDb>()
                    .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                    .UseSluice(provider)
                    .Options
            ));
        });
        await using var shop = new ShopDb(Shop(node.Services).Options);
        await using var helpdesk = new ConfiguringHelpdeskDb(
            new DbContextOptionsBuilder<ConfiguringHelpdeskDb>()
                .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                .UseSluice(node.Services)
                .Options
        );

        var db = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) =>
                    where == "in OnConfiguring"
                        ? r.Db<ConfiguringHelpdeskDb>().Set<HelpTenant>().CountAsync(ct)
                        : r.Db<ShopDb>().Customers.CountAsync(ct)
            );
        var helper = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) =>
                    where == "in OnConfiguring"
                        ? r.Entity(helpdesk.Set<HelpTenant>(), "acme").CountAsync(ct)
                        : r.Entity(shop.Customers, 1).CountAsync(ct)
            );

        if (where == "before UseSluice")
        {
            await db.Should().NotThrowAsync();
            await helper
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage(
                    "*didn't get from read.Db<TContext>()*",
                    "only where it comes from refuses it"
                );
            return;
        }

        var context = where == "in OnConfiguring" ? "ConfiguringHelpdeskDb" : "ShopDb";
        var message =
            $"{context}'s options add the query expression interceptor QueryRewriter after UseSluice (later in the "
            + "options, or in OnConfiguring, which EF runs after the registration's options)*"
            + "Add it before .UseSluice(services), in the same options.";
        await db.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await helper.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void UseSluice_without_AddSluice_throws_saying_to_call_it()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var configure = () => new DbContextOptionsBuilder<ShopDb>().UseSluice(services);

        configure.Should().Throw<InvalidOperationException>().WithMessage("*services.AddSluice()*");
    }

    [Fact]
    public void UseSluice_without_AddSluiceEntityFrameworkCore_throws_saying_to_call_it()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSluice()
            .BuildServiceProvider();

        var configure = () => new DbContextOptionsBuilder<ShopDb>().UseSluice(services);

        configure
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*services.AddSluiceEntityFrameworkCore()*");
    }
}
