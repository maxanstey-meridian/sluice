using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChildrenReadTests(PostgresFixture postgres)
{
    // The sources the model implies for these foreign keys: "<root's table>[<FK columns>]", keyed by the FK value.
    // Declaring them by hand pins those names, which are wire format.
    private static readonly Source<int> _ordersByCustomer = new("Orders[CustomerId]");
    private static readonly Source<int> _notesByCustomer = new("Notes[CustomerId]");
    private static readonly Query<int, string[]> _items = new("ef-children-items");

    private async Task<(int Ada, int Bob)> AddCustomersWithOrdersAndNotes(ShopDb db)
    {
        var (ada, bob) = await db.AddCustomers();
        db.Orders.AddRange(
            new Order { CustomerId = ada, Item = "Tea" },
            new Order { CustomerId = ada, Item = "Cake" },
            new Order { CustomerId = bob, Item = "Coffee" }
        );
        var adaNote = new Note { Text = "Prefers mornings" };
        var bobNote = new Note { Text = "Ring first" };
        db.Notes.AddRange(adaNote, bobNote);
        db.Entry(adaNote).Property("CustomerId").CurrentValue = ada;
        db.Entry(bobNote).Property("CustomerId").CurrentValue = bob;
        await db.SaveChangesAsync();
        return (ada, bob);
    }

    [Fact]
    public async Task Each_Children_form_reads_the_rows_of_its_principal()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomersWithOrdersAndNotes(db);

        var items = await node.Cache.GetOrSetAsync(
            _items,
            ada,
            async (read, ct) =>
            {
                var byProperty = await read.Children(
                        read.Db<ShopDb>().Orders,
                        o => o.CustomerId,
                        ada
                    )
                    .Select(o => o.Item)
                    .ToListAsync(ct);
                var byNavigation = await read.Children(
                        read.Db<ShopDb>().Orders,
                        o => o.Customer,
                        ada
                    )
                    .Select(o => o.Item)
                    .ToListAsync(ct);
                var byShadowKey = await read.Children(
                        read.Db<ShopDb>().Notes,
                        n => EF.Property<int>(n, "CustomerId"),
                        ada
                    )
                    .Select(n => n.Text)
                    .ToListAsync(ct);
                return [.. byProperty.Order(), "|", .. byNavigation.Order(), "|", .. byShadowKey];
            }
        );

        items.Should().Equal("Cake", "Tea", "|", "Cake", "Tea", "|", "Prefers mornings");
    }

    [Fact]
    public async Task Each_Children_form_depends_on_the_foreign_key_value_it_reads()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await AddCustomersWithOrdersAndNotes(db);

        Task ByProperty(SluiceReads read, CancellationToken ct) =>
            read.Children(read.Db<ShopDb>().Orders, o => o.CustomerId, ada).ToListAsync(ct);
        Task ByNavigation(SluiceReads read, CancellationToken ct) =>
            read.Children(read.Db<ShopDb>().Orders, o => o.Customer, ada).ToListAsync(ct);
        Task ByShadowKey(SluiceReads read, CancellationToken ct) =>
            read.Children(read.Db<ShopDb>().Notes, n => EF.Property<int>(n, "CustomerId"), ada)
                .ToListAsync(ct);

        (await CachedRead.IsEvictedBy(ByProperty, _ordersByCustomer.For(ada))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ByProperty, _ordersByCustomer.For(bob))).Should().BeFalse();
        (await CachedRead.IsEvictedBy(ByNavigation, _ordersByCustomer.For(ada))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ByNavigation, _ordersByCustomer.For(bob))).Should().BeFalse();
        (await CachedRead.IsEvictedBy(ByShadowKey, _notesByCustomer.For(ada))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ByShadowKey, _notesByCustomer.For(bob))).Should().BeFalse();
    }

    [Fact]
    public async Task A_Children_read_is_evicted_by_AllDependencies_of_its_set_but_not_by_its_principal()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await AddCustomersWithOrdersAndNotes(db);

        Task Orders(SluiceReads read, CancellationToken ct) =>
            read.Children(read.Db<ShopDb>().Orders, o => o.CustomerId, ada).ToListAsync(ct);
        Task Notes(SluiceReads read, CancellationToken ct) =>
            read.Children(read.Db<ShopDb>().Notes, n => EF.Property<int>(n, "CustomerId"), ada)
                .ToListAsync(ct);

        (await CachedRead.IsEvictedBy(Orders, db.Orders.AllDependencies())).Should().BeTrue();
        (await CachedRead.IsEvictedBy(Notes, db.Notes.AllDependencies())).Should().BeTrue();
        (await CachedRead.IsEvictedBy(Orders, db.Customers.Dependency(ada))).Should().BeFalse();
        (await CachedRead.IsEvictedBy(Orders, db.Notes.AllDependencies())).Should().BeFalse();
    }
}
