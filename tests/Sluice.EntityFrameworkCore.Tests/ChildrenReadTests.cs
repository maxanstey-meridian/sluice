using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChildrenReadTests(PostgresFixture postgres)
{
    private static readonly Query<int, string[]> _items = new("ef-children-items");

    // Whether removing the raw tag `tag` evicts a cached value whose compute runs `read`. The model names a foreign
    // key's source "<root's table>[<FK columns>]" and keys it by the FK value; tags are wire format, so this pins both
    // where an app can't declare a source of that name.
    private static async Task<bool> IsEvictedByTag(
        Func<SluiceReads, CancellationToken, Task> read,
        string tag
    )
    {
        await using var node = new SluiceNode();
        var cached = await CachedRead.Of(node.Cache, read);
        await node.Fusion.RemoveByTagAsync(tag);
        return await cached.WasEvicted();
    }

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

        (await IsEvictedByTag(ByProperty, $"Orders[CustomerId]:{ada}")).Should().BeTrue();
        (await IsEvictedByTag(ByProperty, $"Orders[CustomerId]:{bob}")).Should().BeFalse();
        (await IsEvictedByTag(ByNavigation, $"Orders[CustomerId]:{ada}")).Should().BeTrue();
        (await IsEvictedByTag(ByNavigation, $"Orders[CustomerId]:{bob}")).Should().BeFalse();
        (await IsEvictedByTag(ByShadowKey, $"Notes[CustomerId]:{ada}")).Should().BeTrue();
        (await IsEvictedByTag(ByShadowKey, $"Notes[CustomerId]:{bob}")).Should().BeFalse();
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
