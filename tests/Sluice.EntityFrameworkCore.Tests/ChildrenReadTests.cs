using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChildrenReadTests(PostgresFixture postgres)
{
    // The sources the model implies for these foreign keys: "<root entity type>[<FK properties>]", keyed by the
    // FK value. Tracked writes publish to them; declaring them here stands in for those writes.
    private static readonly Source<int> _ordersByCustomer = new(
        $"{typeof(Order).FullName}[CustomerId]"
    );
    private static readonly Source<int> _notesByCustomer = new(
        $"{typeof(Note).FullName}[CustomerId]"
    );
    private static readonly Query<int, string[]> _items = new("ef-children-items");

    private async Task<(int Ada, int Bob)> AddCustomersWithOrdersAndNotes(ShopDb db)
    {
        var ada = new Customer { Name = "Ada" };
        var bob = new Customer { Name = "Bob" };
        db.Customers.AddRange(ada, bob);
        await db.SaveChangesAsync();

        db.Orders.AddRange(
            new Order { CustomerId = ada.Id, Item = "Tea" },
            new Order { CustomerId = ada.Id, Item = "Cake" },
            new Order { CustomerId = bob.Id, Item = "Coffee" }
        );
        var adaNote = new Note { Text = "Prefers mornings" };
        var bobNote = new Note { Text = "Ring first" };
        db.Notes.AddRange(adaNote, bobNote);
        db.Entry(adaNote).Property("CustomerId").CurrentValue = ada.Id;
        db.Entry(bobNote).Property("CustomerId").CurrentValue = bob.Id;
        await db.SaveChangesAsync();
        return (ada.Id, bob.Id);
    }

    [Fact]
    public async Task Each_Children_form_reads_the_rows_of_its_principal()
    {
        await using var db = postgres.CreateDb();
        var (ada, _) = await AddCustomersWithOrdersAndNotes(db);
        await using var node = new SluiceNode();

        var items = await node.Cache.GetOrSetAsync(
            _items,
            ada,
            async (read, ct) =>
            {
                var byProperty = await read.Children(db.Orders, o => o.CustomerId, ada)
                    .Select(o => o.Item)
                    .ToListAsync(ct);
                var byNavigation = await read.Children(db.Orders, o => o.Customer, ada)
                    .Select(o => o.Item)
                    .ToListAsync(ct);
                var byShadowKey = await read.Children(
                        db.Notes,
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
        await using var db = postgres.CreateDb();
        var (ada, bob) = await AddCustomersWithOrdersAndNotes(db);

        Task ByProperty(SluiceReads read, CancellationToken ct) =>
            read.Children(db.Orders, o => o.CustomerId, ada).ToListAsync(ct);
        Task ByNavigation(SluiceReads read, CancellationToken ct) =>
            read.Children(db.Orders, o => o.Customer, ada).ToListAsync(ct);
        Task ByShadowKey(SluiceReads read, CancellationToken ct) =>
            read.Children(db.Notes, n => EF.Property<int>(n, "CustomerId"), ada).ToListAsync(ct);

        (await Eviction.IsEvictedBy(ByProperty, _ordersByCustomer.For(ada))).Should().BeTrue();
        (await Eviction.IsEvictedBy(ByProperty, _ordersByCustomer.For(bob))).Should().BeFalse();
        (await Eviction.IsEvictedBy(ByNavigation, _ordersByCustomer.For(ada))).Should().BeTrue();
        (await Eviction.IsEvictedBy(ByNavigation, _ordersByCustomer.For(bob))).Should().BeFalse();
        (await Eviction.IsEvictedBy(ByShadowKey, _notesByCustomer.For(ada))).Should().BeTrue();
        (await Eviction.IsEvictedBy(ByShadowKey, _notesByCustomer.For(bob))).Should().BeFalse();
    }

    [Fact]
    public async Task A_Children_read_is_evicted_by_AllDependencies_of_its_set_but_not_by_its_principal()
    {
        await using var db = postgres.CreateDb();
        var (ada, _) = await AddCustomersWithOrdersAndNotes(db);

        Task Orders(SluiceReads read, CancellationToken ct) =>
            read.Children(db.Orders, o => o.CustomerId, ada).ToListAsync(ct);
        Task Notes(SluiceReads read, CancellationToken ct) =>
            read.Children(db.Notes, n => EF.Property<int>(n, "CustomerId"), ada).ToListAsync(ct);

        (await Eviction.IsEvictedBy(Orders, db.Orders.AllDependencies())).Should().BeTrue();
        (await Eviction.IsEvictedBy(Notes, db.Notes.AllDependencies())).Should().BeTrue();
        (await Eviction.IsEvictedBy(Orders, db.Customers.Dependency(ada))).Should().BeFalse();
        (await Eviction.IsEvictedBy(Orders, db.Customers.AllDependencies())).Should().BeFalse();
        (await Eviction.IsEvictedBy(Orders, db.Notes.AllDependencies())).Should().BeFalse();
    }
}
