using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReadGuardTests(PostgresFixture postgres)
{
    private static readonly Query<int, string> _name = new("ef-guard-name");

    [Fact]
    public async Task A_key_of_the_wrong_type_throws_naming_the_expected_type()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var longForIntKey = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Entity(r.Db<ShopDb>().Customers, 1L).ToListAsync(ct)
            );
        var rawValueForTypedId = () =>
            CachedRead.Of(node.Cache, (r, ct) => r.Entity(r.Db<ShopDb>().Users, 7).ToListAsync(ct));
        var stringForIntForeignKey = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Children(r.Db<ShopDb>().Orders, o => o.CustomerId, "1").ToListAsync(ct)
            );
        var longForIntDependency = () => db.Customers.Dependency(1L);

        await longForIntKey
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*must be of type Int32*was of type Int64*");
        await rawValueForTypedId
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*must be of type UserId*was of type Int32*");
        await stringForIntForeignKey
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*must be of type Int32*was of type String*");
        longForIntDependency
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*must be of type Int32*was of type Int64*");
    }

    [Fact]
    public async Task A_nullable_foreign_key_takes_a_key_of_its_underlying_type()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var read = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Children(r.Db<ShopDb>().OrderLines, l => l.CouponId, 1).ToListAsync(ct)
            );

        await read.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_selector_that_is_not_a_single_column_foreign_key_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var plainProperty = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Children(r.Db<ShopDb>().Orders, o => o.Item, "Tea").ToListAsync(ct)
            );
        var primaryKey = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Children(r.Db<ShopDb>().Orders, o => o.Id, 1).ToListAsync(ct)
            );
        var memberOfNavigation = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) =>
                    r.Children(r.Db<ShopDb>().Orders, o => o.Customer!.Name, "Ada").ToListAsync(ct)
            );

        await plainProperty
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*single-column foreign key*");
        await primaryKey
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*single-column foreign key*");
        await memberOfNavigation
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*single-column foreign key*");
    }

    [Fact]
    public async Task A_shadow_foreign_key_named_by_a_variable_throws_asking_for_a_constant_name()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var name = "CustomerId";

        var read = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) =>
                    r.Children(r.Db<ShopDb>().Notes, n => EF.Property<int>(n, name), 1)
                        .ToListAsync(ct)
            );

        await read.Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*EF.Property must be a constant string*")
            .Where(exception => exception.ParamName == "foreignKey");
    }

    [Fact]
    public async Task Reads_on_a_context_without_UseSluice_throw_naming_it()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb();

        // Configuring a context with UseSluice starts Sluice's watch on every EF context.
        await using var tracked = postgres.CreateDb(node);
        var made = new MadeContexts(
            new DbContextOptionsBuilder<ShopDb>().UseNpgsql(postgres.ConnectionString).Options
        );
        await using var withoutSluice = new SluiceNode(services: services =>
            services.AddScoped(_ => made.CreateDbContext())
        );

        var entity = () =>
            CachedRead.Of(node.Cache, (r, ct) => r.Entity(db.Customers, 1).ToListAsync(ct));
        var children = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Children(db.Orders, o => o.CustomerId, 1).ToListAsync(ct)
            );
        var factory = () =>
            CachedRead.Of(withoutSluice.Cache, (r, ct) => r.Db<ShopDb>().Customers.ToListAsync(ct));
        var plain = () => CachedRead.Of(node.Cache, (_, ct) => db.Customers.ToListAsync(ct));

        await entity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseSluice*");
        await children.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseSluice*");
        await factory.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseSluice*");
        made.Context!.Invoking(context => context.Model)
            .Should()
            .Throw<ObjectDisposedException>(
                "the compute's scope disposes the context read.Db refused"
            );
        await plain
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "*ran inside a compute on a ShopDb, which isn't configured with UseSluice*"
            );
    }

    [Fact]
    public async Task Read_Db_without_the_context_registered_throws_saying_how_to_register_it()
    {
        // A factory alone isn't enough: read.Db takes the context itself from the compute's scope.
        await using var node = new SluiceNode(services: services =>
            services.AddSingleton<IDbContextFactory<ShopDb>>(
                new MadeContexts(
                    new DbContextOptionsBuilder<ShopDb>()
                        .UseNpgsql(postgres.ConnectionString)
                        .Options
                )
            )
        );

        var read = () =>
            CachedRead.Of(node.Cache, (r, ct) => r.Db<ShopDb>().Customers.ToListAsync(ct));

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*none is registered*AddDbContext<ShopDb>*");
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Read_Db_takes_a_context_registered_with_AddDbContext(ServiceLifetime lifetime)
    {
        await using var node = new SluiceNode(services: services =>
            services.AddDbContext<ShopDb>(
                (provider, options) =>
                    options.UseNpgsql(postgres.ConnectionString).UseSluice(provider),
                lifetime
            )
        );
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();

        var adaRead = await CachedRead.Customer(node.Cache, ada);
        await db.Rename(ada, "Ada Lovelace");

        (await adaRead.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Reading_or_invalidating_one_row_by_composite_key_throws_but_the_whole_set_works()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var read = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Entity(r.Db<ShopDb>().OrderLines, 1).ToListAsync(ct)
            );
        var dependency = () => db.OrderLines.Dependency(1);
        var readAll = () =>
            CachedRead.Of(node.Cache, (r, ct) => r.Db<ShopDb>().OrderLines.ToListAsync(ct));

        await read.Should().ThrowAsync<NotSupportedException>().WithMessage("*composite*");
        dependency.Should().Throw<NotSupportedException>().WithMessage("*composite*");
        await readAll.Should().NotThrowAsync();
        db.OrderLines.AllDependencies().Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_compute_reads_what_is_committed_while_the_caller_s_EF_transaction_is_open()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Rename(ada, "Uncommitted");

        var name = await node.Cache.GetOrSetAsync(
            _name,
            ada,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Customers, ada).Select(c => c.Name).SingleAsync(ct)
        );

        name.Should().Be("Ada");
    }

    [Fact]
    public async Task A_compute_reads_what_is_committed_inside_an_enlisted_transaction_or_a_TransactionScope()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();
        using var enlisted = new CommittableTransaction();
        await db.Database.OpenConnectionAsync();
        db.Database.EnlistTransaction(enlisted);
        await db.Rename(ada, "Uncommitted");

        Task<string> NameOf(int id) =>
            node
                .Cache.GetOrSetAsync(
                    _name,
                    id,
                    (read, ct) =>
                        read.Entity(read.Db<ShopDb>().Customers, id)
                            .Select(c => c.Name)
                            .SingleAsync(ct)
                )
                .AsTask();

        (await NameOf(ada)).Should().Be("Ada");
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (await NameOf(bob)).Should().Be("Bob", "the compute's connection doesn't enlist");
            Transaction
                .Current.Should()
                .NotBeNull("the caller's scope is back once the compute ends");
        }
    }

    [Fact]
    public async Task Helper_reads_and_dependencies_of_types_not_mapped_to_a_table_throw()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var tableReadThroughAView = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Entity(r.Db<ShopDb>().Suppliers, 1).ToListAsync(ct)
            );
        var noTable = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Entity(r.Db<ShopDb>().Unstored, 1).ToListAsync(ct)
            );
        var keyless = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) =>
                    r.Children(r.Db<ShopDb>().CustomerTotals, t => t.CustomerId, 1).ToListAsync(ct)
            );

        await tableReadThroughAView
            .Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*view 'supplier_names'*");
        await noTable
            .Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("Unstored isn't mapped to a table*");
        await keyless.Should().ThrowAsync<NotSupportedException>().WithMessage("*keyless*");
        db.Invoking(d => d.CustomerTotals.AllDependencies())
            .Should()
            .Throw<NotSupportedException>();
        db.Invoking(d => d.Suppliers.Dependency(1)).Should().Throw<NotSupportedException>();
        db.Invoking(d => d.Suppliers.AllDependencies()).Should().Throw<NotSupportedException>();
        db.Invoking(d => d.Promotions.AllDependencies()).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task A_type_stored_in_a_table_with_a_derived_type_mapped_to_a_view_is_refused()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        const string View =
            "the derived type LiveBanner, which is mapped to the view 'live_banners'";

        var helper = () =>
            CachedRead.Of(
                node.Cache,
                (r, ct) => r.Entity(r.Db<ShopDb>().Banners, 1).ToListAsync(ct)
            );
        var plain = () =>
            CachedRead.Of(node.Cache, (r, ct) => r.Db<ShopDb>().Banners.ToListAsync(ct));

        await helper
            .Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage($"Banner has {View}*");
        await plain
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*it reads Banner, which has {View}*");
        db.Invoking(d => d.Banners.Dependency(1)).Should().Throw<NotSupportedException>();
        db.Invoking(d => d.Banners.AllDependencies()).Should().Throw<NotSupportedException>();
    }

    // A factory that keeps the last context it made.
    private sealed class MadeContexts(DbContextOptions<ShopDb> options) : IDbContextFactory<ShopDb>
    {
        public ShopDb? Context { get; private set; }

        public ShopDb CreateDbContext() => Context = new(options);
    }
}
