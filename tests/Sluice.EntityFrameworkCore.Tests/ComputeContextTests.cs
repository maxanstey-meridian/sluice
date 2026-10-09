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

    [Fact]
    public void UseSluice_without_AddSluice_throws_saying_to_call_it()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var configure = () => new DbContextOptionsBuilder<ShopDb>().UseSluice(services);

        configure.Should().Throw<InvalidOperationException>().WithMessage("*services.AddSluice()*");
    }
}
