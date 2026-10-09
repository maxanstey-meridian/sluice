using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReadGuardTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_key_of_the_wrong_type_throws_naming_the_expected_type()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var longForIntKey = () =>
            InCompute.Run((r, ct) => r.Entity(db.Customers, 1L).ToListAsync(ct));
        var rawValueForTypedId = () =>
            InCompute.Run((r, ct) => r.Entity(db.Users, 7).ToListAsync(ct));
        var stringForIntForeignKey = () =>
            InCompute.Run((r, ct) => r.Children(db.Orders, o => o.CustomerId, "1").ToListAsync(ct));
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
            InCompute.Run((r, ct) => r.Children(db.OrderLines, l => l.CouponId, 1).ToListAsync(ct));

        await read.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_selector_that_is_not_a_single_column_foreign_key_throws()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var plainProperty = () =>
            InCompute.Run((r, ct) => r.Children(db.Orders, o => o.Item, "Tea").ToListAsync(ct));
        var primaryKey = () =>
            InCompute.Run((r, ct) => r.Children(db.Orders, o => o.Id, 1).ToListAsync(ct));
        var memberOfNavigation = () =>
            InCompute.Run(
                (r, ct) => r.Children(db.Orders, o => o.Customer!.Name, "Ada").ToListAsync(ct)
            );

        await plainProperty
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*single-column foreign key*");
        await primaryKey.Should().ThrowAsync<ArgumentException>();
        await memberOfNavigation.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_shadow_foreign_key_named_by_a_variable_throws_asking_for_a_constant_name()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var name = "CustomerId";

        var read = () =>
            InCompute.Run(
                (r, ct) => r.Children(db.Notes, n => EF.Property<int>(n, name), 1).ToListAsync(ct)
            );

        await read.Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*EF.Property must be a constant string*")
            .Where(exception => exception.ParamName == "foreignKey");
    }

    [Fact]
    public async Task Reads_on_a_context_without_UseSluice_throw()
    {
        await using var db = postgres.CreateDb();

        var entity = () => InCompute.Run((r, ct) => r.Entity(db.Customers, 1).ToListAsync(ct));
        var children = () =>
            InCompute.Run((r, ct) => r.Children(db.Orders, o => o.CustomerId, 1).ToListAsync(ct));
        var all = () => InCompute.Run((r, ct) => r.All(db.Customers).ToListAsync(ct));

        await entity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseSluice*");
        await children.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseSluice*");
        await all.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseSluice*");
    }

    [Fact]
    public async Task Reading_or_invalidating_one_row_by_composite_key_throws_but_the_whole_set_works()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var read = () => InCompute.Run((r, ct) => r.Entity(db.OrderLines, 1).ToListAsync(ct));
        var dependency = () => db.OrderLines.Dependency(1);
        var readAll = () => InCompute.Run((r, ct) => r.All(db.OrderLines).ToListAsync(ct));

        await read.Should().ThrowAsync<NotSupportedException>().WithMessage("*composite*");
        dependency.Should().Throw<NotSupportedException>().WithMessage("*composite*");
        await readAll.Should().NotThrowAsync();
        db.OrderLines.AllDependencies().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reads_throw_inside_an_EF_transaction_and_run_again_after_it_commits()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var customer = new Customer { Name = "Ada" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var transaction = await db.Database.BeginTransactionAsync();

        var entity = () =>
            InCompute.Run((r, ct) => r.Entity(db.Customers, customer.Id).ToListAsync(ct));
        var children = () =>
            InCompute.Run(
                (r, ct) => r.Children(db.Orders, o => o.CustomerId, customer.Id).ToListAsync(ct)
            );
        var all = () => InCompute.Run((r, ct) => r.All(db.Customers).ToListAsync(ct));

        await entity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transaction*");
        await children
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*transaction*");
        await all.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transaction*");

        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await entity.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Reads_throw_inside_an_enlisted_transaction()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        using var transaction = new CommittableTransaction();
        await db.Database.OpenConnectionAsync();
        db.Database.EnlistTransaction(transaction);

        var read = () => InCompute.Run((r, ct) => r.All(db.Customers).ToListAsync(ct));

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transaction*");
    }

    [Fact]
    public async Task Reads_throw_inside_a_TransactionScope()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

        var read = () => InCompute.Run((r, ct) => r.All(db.Customers).ToListAsync(ct));

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transaction*");
    }

    [Fact]
    public async Task Reads_run_inside_a_TransactionScope_when_the_connection_does_not_enlist()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node, enlist: false);
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

        var read = () => InCompute.Run((r, ct) => r.All(db.Customers).ToListAsync(ct));

        await read.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Reads_and_dependencies_of_types_not_mapped_to_a_table_throw()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        var keylessView = () => InCompute.Run((r, ct) => r.All(db.CustomerTotals).ToListAsync(ct));
        var view = () => InCompute.Run((r, ct) => r.Entity(db.ActiveCustomers, 1).ToListAsync(ct));
        var sqlQuery = () => InCompute.Run((r, ct) => r.All(db.TopCustomers).ToListAsync(ct));
        var function = () => InCompute.Run((r, ct) => r.All(db.RecentOrders).ToListAsync(ct));

        await keylessView.Should().ThrowAsync<NotSupportedException>().WithMessage("*keyless*");
        await view.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*view 'active_customers'*");
        await sqlQuery.Should().ThrowAsync<NotSupportedException>().WithMessage("*SQL query*");
        await function
            .Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*function 'recent_orders'*");
        db.Invoking(d => d.CustomerTotals.AllDependencies())
            .Should()
            .Throw<NotSupportedException>();
        db.Invoking(d => d.ActiveCustomers.Dependency(1)).Should().Throw<NotSupportedException>();
        db.Invoking(d => d.ActiveCustomers.AllDependencies())
            .Should()
            .Throw<NotSupportedException>();
    }
}
