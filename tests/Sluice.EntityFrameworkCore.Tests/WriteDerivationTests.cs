using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>What a tracked save evicts. Reads run on their own context; writes on one wired with <c>UseSluice</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WriteDerivationTests : IAsyncLifetime
{
    private readonly SluiceNode _node = new();
    private readonly ShopDb _reader;
    private readonly ShopDb _db;

    public WriteDerivationTests(PostgresFixture postgres)
    {
        _reader = postgres.CreateDb();
        _db = postgres.CreateDb(_node);
    }

    // xUnit 2 disposes a test class through IAsyncLifetime, never through IAsyncDisposable.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _reader.DisposeAsync();
        await _node.DisposeAsync();
    }

    private async Task<Customer> AddCustomer(string name)
    {
        var customer = new Customer { Name = name };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    private async Task<Order> AddOrder(Customer customer, string item)
    {
        var order = new Order { CustomerId = customer.Id, Item = item };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    private Task<CachedRead> ReadCustomer(int id) => CachedRead.Customer(_node.Cache, _reader, id);

    private Task<CachedRead> ReadOrdersOf(int customerId) =>
        CachedRead.OrdersOf(_node.Cache, _reader, customerId);

    [Fact]
    public async Task Saving_an_entity_evicts_readers_of_its_key_and_only_those()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var adaRead = await ReadCustomer(ada.Id);
        var bobRead = await ReadCustomer(bob.Id);

        ada.Name = "Ada Lovelace";
        await _db.SaveChangesAsync();

        (await adaRead.WasEvicted()).Should().BeTrue("Ada was edited");
        (await bobRead.WasEvicted()).Should().BeFalse();

        _db.Customers.Remove(ada);
        await _db.SaveChangesAsync();

        (await adaRead.WasEvicted()).Should().BeTrue("Ada was deleted");
        (await bobRead.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task Adding_editing_or_deleting_a_child_evicts_Children_readers_of_its_parent_only()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var adaOrders = await ReadOrdersOf(ada.Id);
        var bobOrders = await ReadOrdersOf(bob.Id);

        var tea = await AddOrder(ada, "Tea");
        (await adaOrders.WasEvicted()).Should().BeTrue("an order was added");
        (await bobOrders.WasEvicted()).Should().BeFalse();

        tea.Item = "Green tea";
        await _db.SaveChangesAsync();
        (await adaOrders.WasEvicted()).Should().BeTrue("an order was edited");
        (await bobOrders.WasEvicted()).Should().BeFalse();

        _db.Orders.Remove(tea);
        await _db.SaveChangesAsync();
        (await adaOrders.WasEvicted()).Should().BeTrue("an order was deleted");
        (await bobOrders.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task Re_parenting_a_child_evicts_Children_readers_of_both_parents()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var cy = await AddCustomer("Cy");
        var tea = await AddOrder(ada, "Tea");
        var adaOrders = await ReadOrdersOf(ada.Id);
        var bobOrders = await ReadOrdersOf(bob.Id);
        var cyOrders = await ReadOrdersOf(cy.Id);

        tea.CustomerId = bob.Id;
        await _db.SaveChangesAsync();

        (await adaOrders.WasEvicted()).Should().BeTrue("the order left Ada");
        (await bobOrders.WasEvicted()).Should().BeTrue("the order joined Bob");
        (await cyOrders.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_row_that_was_never_loaded_evicts_Children_readers_of_its_parent()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var adaOrders = await ReadOrdersOf(ada.Id);
        _db.ChangeTracker.Clear();

        _db.Orders.Remove(new Order { Id = tea.Id });
        await _db.SaveChangesAsync();

        (await _reader.Orders.AnyAsync(o => o.Id == tea.Id)).Should().BeFalse();
        (await adaOrders.WasEvicted()).Should().BeTrue("Ada's order was deleted");
    }

    [Fact]
    public async Task Removing_a_row_that_was_never_loaded_with_its_required_foreign_key_set_to_null_evicts_Children_readers_of_its_parent()
    {
        var north = new Warehouse { Code = $"N-{Guid.NewGuid():N}" };
        var bin = new Bin { WarehouseCode = north.Code };
        _db.AddRange(north, bin);
        await _db.SaveChangesAsync();
        var northBins = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(_reader.Bins, b => b.WarehouseCode, north.Code).ToListAsync(ct)
        );
        _db.ChangeTracker.Clear();

        _db.Bins.Remove(new Bin { Id = bin.Id, WarehouseCode = null! });
        await _db.SaveChangesAsync();

        (await _reader.Bins.AnyAsync(b => b.Id == bin.Id)).Should().BeFalse();
        (await northBins.WasEvicted()).Should().BeTrue("the north warehouse's bin was deleted");
    }

    [Fact]
    public async Task Updating_a_row_from_an_object_that_was_never_loaded_evicts_Children_readers_of_its_old_parent()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var tea = await AddOrder(ada, "Tea");
        var adaOrders = await ReadOrdersOf(ada.Id);
        var bobOrders = await ReadOrdersOf(bob.Id);
        _db.ChangeTracker.Clear();

        _db.Orders.Update(
            new Order
            {
                Id = tea.Id,
                CustomerId = bob.Id,
                Item = "Tea",
            }
        );
        await _db.SaveChangesAsync();

        (await _reader.Orders.SingleAsync(o => o.Id == tea.Id)).CustomerId.Should().Be(bob.Id);
        (await adaOrders.WasEvicted()).Should().BeTrue("the order left Ada");
        (await bobOrders.WasEvicted()).Should().BeTrue("the order joined Bob");
    }

    [Fact]
    public async Task A_partial_update_of_a_row_that_was_never_loaded_evicts_Children_readers_of_its_parent()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var adaOrders = await ReadOrdersOf(ada.Id);
        _db.ChangeTracker.Clear();

        var stub = new Order { Id = tea.Id, Item = "Green tea" };
        _db.Orders.Attach(stub);
        _db.Entry(stub).Property(o => o.Item).IsModified = true;
        await _db.SaveChangesAsync();

        (await _reader.Orders.SingleAsync(o => o.Id == tea.Id)).Item.Should().Be("Green tea");
        (await adaOrders.WasEvicted()).Should().BeTrue("Ada's order was edited");
    }

    [Fact]
    public async Task Re_parenting_through_a_navigation_or_a_shadow_foreign_key_evicts_every_Children_form_of_both_parents()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var tea = await AddOrder(ada, "Tea");
        var note = new Note { Text = "Ring first" };
        _db.Notes.Add(note);
        _db.Entry(note).Property("CustomerId").CurrentValue = ada.Id;
        await _db.SaveChangesAsync();
        var readers = new List<(string Form, CachedRead Read)>();
        foreach (var parent in new[] { ada.Id, bob.Id })
        {
            readers.Add(
                (
                    $"orders of {parent} by navigation",
                    await CachedRead.Of(
                        _node.Cache,
                        (read, ct) =>
                            read.Children(_reader.Orders, o => o.Customer, parent).ToListAsync(ct)
                    )
                )
            );
            readers.Add(($"orders of {parent} by foreign key", await ReadOrdersOf(parent)));
            readers.Add(
                (
                    $"notes of {parent} by shadow foreign key",
                    await CachedRead.Of(
                        _node.Cache,
                        (read, ct) =>
                            read.Children(
                                    _reader.Notes,
                                    n => EF.Property<int>(n, "CustomerId"),
                                    parent
                                )
                                .ToListAsync(ct)
                    )
                )
            );
        }

        tea.Customer = bob;
        _db.Entry(note).Property("CustomerId").CurrentValue = bob.Id;
        await _db.SaveChangesAsync();

        tea.CustomerId.Should().Be(bob.Id, "EF set the foreign key from the navigation");
        foreach (var (form, read) in readers)
        {
            (await read.WasEvicted()).Should().BeTrue(form);
        }
    }

    [Fact]
    public async Task Setting_a_nullable_foreign_key_to_or_from_null_evicts_Children_readers_of_its_principal()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var coupon = new Coupon();
        var other = new Coupon();
        _db.Coupons.AddRange(coupon, other);
        var line = new OrderLine { OrderId = tea.Id, LineNumber = 1 };
        _db.OrderLines.Add(line);
        await _db.SaveChangesAsync();
        var couponLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(_reader.OrderLines, l => l.CouponId, coupon.Id).ToListAsync(ct)
        );
        var otherLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(_reader.OrderLines, l => l.CouponId, other.Id).ToListAsync(ct)
        );

        line.CouponId = coupon.Id;
        await _db.SaveChangesAsync();
        (await couponLines.WasEvicted()).Should().BeTrue("the line gained the coupon");

        line.CouponId = null;
        await _db.SaveChangesAsync();
        (await couponLines.WasEvicted()).Should().BeTrue("the line lost the coupon");
        (await otherLines.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task A_foreign_key_to_an_alternate_key_evicts_Children_readers_of_that_key()
    {
        var north = new Warehouse { Code = $"N-{Guid.NewGuid():N}" };
        var south = new Warehouse { Code = $"S-{Guid.NewGuid():N}" };
        _db.Warehouses.AddRange(north, south);
        await _db.SaveChangesAsync();
        Task<CachedRead> ReadBinsOf(string code) =>
            CachedRead.Of(
                _node.Cache,
                (read, ct) =>
                    read.Children(_reader.Bins, b => b.WarehouseCode, code).ToListAsync(ct)
            );
        var northBins = await ReadBinsOf(north.Code);
        var southBins = await ReadBinsOf(south.Code);

        var bin = new Bin { WarehouseCode = north.Code };
        _db.Bins.Add(bin);
        await _db.SaveChangesAsync();
        (await northBins.WasEvicted()).Should().BeTrue("a bin was added to the north warehouse");
        (await southBins.WasEvicted()).Should().BeFalse();

        bin.WarehouseCode = south.Code;
        await _db.SaveChangesAsync();
        (await northBins.WasEvicted()).Should().BeTrue("the bin left the north warehouse");
        (await southBins.WasEvicted()).Should().BeTrue("the bin joined the south warehouse");

        // Only the warehouse is tracked, so the database's cascade deletes the bin.
        _db.ChangeTracker.Clear();
        _db.Warehouses.Remove(await _db.Warehouses.SingleAsync(w => w.Id == south.Id));
        await _db.SaveChangesAsync();
        (await southBins.WasEvicted()).Should().BeTrue("the database deleted the bin");
        (await _reader.Bins.AnyAsync(b => b.Id == bin.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Linking_or_unlinking_many_to_many_rows_evicts_Children_readers_of_the_join_rows_on_both_sides()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var coupon = new Coupon();
        _db.Coupons.Add(coupon);
        await _db.SaveChangesAsync();
        Task<CachedRead> ReadLinksBy(
            Expression<Func<Dictionary<string, object>, object?>> foreignKey,
            int key
        ) =>
            CachedRead.Of(
                _node.Cache,
                (read, ct) =>
                    read.Children(_reader.CustomerCoupons, foreignKey, key).ToListAsync(ct)
            );
        var adaLinks = await ReadLinksBy(link => EF.Property<int>(link, "CustomerId"), ada.Id);
        var bobLinks = await ReadLinksBy(link => EF.Property<int>(link, "CustomerId"), bob.Id);
        var couponLinks = await ReadLinksBy(link => EF.Property<int>(link, "CouponId"), coupon.Id);

        ada.Coupons.Add(coupon);
        await _db.SaveChangesAsync();
        (await adaLinks.WasEvicted()).Should().BeTrue("Ada gained the coupon");
        (await couponLinks.WasEvicted()).Should().BeTrue("the coupon gained Ada");
        (await bobLinks.WasEvicted()).Should().BeFalse();

        ada.Coupons.Remove(coupon);
        await _db.SaveChangesAsync();
        (await adaLinks.WasEvicted()).Should().BeTrue("Ada lost the coupon");
        (await couponLinks.WasEvicted()).Should().BeTrue("the coupon lost Ada");
        (await bobLinks.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task Changing_an_owned_value_evicts_readers_of_its_owner_though_the_tracker_leaves_the_owner_unchanged()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var parcel = new Parcel
        {
            CustomerId = ada.Id,
            Address = new()
            {
                Street = "1 Main St",
                Contact = new() { Phone = "555-0100" },
            },
            Stops = [new() { Place = "Depot" }],
        };
        var other = new Parcel { CustomerId = bob.Id };
        _db.Parcels.AddRange(parcel, other);
        await _db.SaveChangesAsync();
        var parcelRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Parcels, parcel.Id).SingleAsync(ct)
        );
        var adaParcels = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Children(_reader.Parcels, p => p.CustomerId, ada.Id).ToListAsync(ct)
        );
        var otherRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Parcels, other.Id).SingleAsync(ct)
        );

        async Task SaveAndExpectEviction(string change)
        {
            _db.ChangeTracker.DetectChanges();
            _db.Entry(parcel).State.Should().Be(EntityState.Unchanged, "only {0} changed", change);
            await _db.SaveChangesAsync();
            (await parcelRead.WasEvicted()).Should().BeTrue("{0} changed", change);
            (await adaParcels.WasEvicted()).Should().BeTrue("{0} changed", change);
            (await otherRead.WasEvicted()).Should().BeFalse();
        }

        parcel.Address.Contact.Phone = "555-0199";
        await SaveAndExpectEviction("the contact, owned by the owned address,");

        parcel.Stops.Add(new() { Place = "Hub" });
        await SaveAndExpectEviction("the owned stops");

        parcel.Stops.RemoveAt(0);
        await SaveAndExpectEviction("the owned stops");
    }

    [Fact]
    public async Task Editing_a_derived_row_evicts_readers_of_its_root_type_with_TPH_or_TPT()
    {
        var rex = new Dog { Name = "Rex" };
        var truck = new Truck { Plate = "AB12 CDE", Axles = 2 };
        _db.AddRange(rex, truck);
        await _db.SaveChangesAsync();
        var rexRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Animals, rex.Id).SingleAsync(ct)
        );
        var animals = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.All(_reader.Animals).ToListAsync(ct)
        );
        var truckRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Vehicles, truck.Id).SingleAsync(ct)
        );
        var vehicles = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.All(_reader.Vehicles).ToListAsync(ct)
        );

        rex.Fetches = true;
        await _db.SaveChangesAsync();
        (await rexRead.WasEvicted()).Should().BeTrue("a column of the derived dog changed");
        (await animals.WasEvicted()).Should().BeTrue("a column of the derived dog changed");
        (await vehicles.WasEvicted()).Should().BeFalse();

        truck.Axles = 3;
        await _db.SaveChangesAsync();
        (await truckRead.WasEvicted()).Should().BeTrue("a column in the truck's own table changed");
        (await vehicles.WasEvicted()).Should().BeTrue("a column in the truck's own table changed");
        (await animals.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task Any_write_to_a_type_evicts_its_All_readers_and_a_write_to_another_type_does_not()
    {
        var all = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.All(_reader.Customers).ToListAsync(ct)
        );

        var ada = await AddCustomer("Ada");
        (await all.WasEvicted()).Should().BeTrue("a customer was added");

        await AddOrder(ada, "Tea");
        (await all.WasEvicted()).Should().BeFalse("only an order was written");

        ada.Name = "Ada Lovelace";
        await _db.SaveChangesAsync();
        (await all.WasEvicted()).Should().BeTrue("a customer was edited");

        _db.Remove(ada);
        await _db.SaveChangesAsync();
        (await all.WasEvicted()).Should().BeTrue("a customer was deleted");
    }

    [Fact]
    public async Task Inserting_rows_evicts_earlier_not_found_reads_of_their_generated_keys()
    {
        var existing = await AddCustomer("Bob");
        var existingRead = await ReadCustomer(existing.Id);
        var next = await _reader.NextCustomerId();
        var customerRead = await ReadCustomer(next);
        var ordersRead = await ReadOrdersOf(next);

        // One save: the order's foreign key is temporary until the customer's key is generated.
        var customer = new Customer { Name = "Ada" };
        _db.Orders.Add(new Order { Customer = customer, Item = "Tea" });
        await _db.SaveChangesAsync();

        customer.Id.Should().Be(next, "the test predicted the generated key");
        (await customerRead.WasEvicted()).Should().BeTrue();
        (await ordersRead.WasEvicted()).Should().BeTrue();
        (await existingRead.WasEvicted())
            .Should()
            .BeFalse("only the new customer's key was written");
    }

    [Fact]
    public async Task A_write_through_a_value_converter_evicts_readers_of_the_same_id()
    {
        var graceId = new UserId(await _reader.UnusedUserId());
        var alanId = new UserId(graceId.Value + 1);
        var sku = $"Tea-{Guid.NewGuid():N}";
        var grace = new User { Id = graceId, Name = "Grace" };
        var alan = new User { Id = alanId, Name = "Alan" };
        var tea = new Product { Sku = new Sku(sku), Name = "Tea" };
        _db.AddRange(grace, alan, tea);
        await _db.SaveChangesAsync();
        var graceRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Users, graceId).SingleAsync(ct)
        );
        var alanRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Users, alanId).SingleAsync(ct)
        );
        var teaRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Entity(_reader.Products, new Sku(sku.ToUpperInvariant())).SingleAsync(ct)
        );

        grace.Name = "Grace Hopper";
        tea.Name = "Black tea";
        await _db.SaveChangesAsync();

        (await graceRead.WasEvicted()).Should().BeTrue();
        (await alanRead.WasEvicted()).Should().BeFalse();
        (await teaRead.WasEvicted())
            .Should()
            .BeTrue("citext compares the SKU in either case as equal");
    }

    [Fact]
    public async Task Writing_a_composite_key_row_evicts_its_Children_and_All_readers()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var coffee = await AddOrder(ada, "Coffee");
        var teaLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Children(_reader.OrderLines, l => l.OrderId, tea.Id).ToListAsync(ct)
        );
        var coffeeLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(_reader.OrderLines, l => l.OrderId, coffee.Id).ToListAsync(ct)
        );
        var allLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.All(_reader.OrderLines).ToListAsync(ct)
        );

        _db.OrderLines.Add(new OrderLine { OrderId = tea.Id, LineNumber = 1 });
        await _db.SaveChangesAsync();

        (await teaLines.WasEvicted()).Should().BeTrue();
        (await allLines.WasEvicted()).Should().BeTrue();
        (await coffeeLines.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task A_save_that_also_writes_a_type_reads_refuse_still_evicts_the_rest()
    {
        var ada = await AddCustomer("Ada");
        var adaRead = await ReadCustomer(ada.Id);

        ada.Name = "Ada Lovelace";
        _db.Suppliers.Add(new Supplier { Name = "Acme" });
        await _db.SaveChangesAsync();

        (await adaRead.WasEvicted()).Should().BeTrue();
        _node.Logs.GetSnapshot().Should().NotContain(record => record.Exception != null);
    }

    [Fact]
    public async Task Deleting_a_principal_evicts_Children_readers_of_rows_the_database_deletes_or_sets_to_null()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var coupon = new Coupon();
        _db.Coupons.Add(coupon);
        await _db.SaveChangesAsync();
        _db.OrderLines.Add(
            new OrderLine
            {
                OrderId = tea.Id,
                LineNumber = 1,
                CouponId = coupon.Id,
            }
        );
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var adaOrders = await ReadOrdersOf(ada.Id);
        var couponLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(_reader.OrderLines, l => l.CouponId, coupon.Id).ToListAsync(ct)
        );

        // Only the principals are tracked, so the database's ON DELETE rules change the dependents.
        _db.Coupons.Remove(await _db.Coupons.SingleAsync(c => c.Id == coupon.Id));
        await _db.SaveChangesAsync();
        (await couponLines.WasEvicted())
            .Should()
            .BeTrue("the database set the line's coupon to null");

        _db.Customers.Remove(await _db.Customers.SingleAsync(c => c.Id == ada.Id));
        await _db.SaveChangesAsync();
        (await adaOrders.WasEvicted()).Should().BeTrue("the database deleted Ada's orders");
        (await _reader.Orders.AnyAsync(o => o.Id == tea.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task With_cascades_on_save_deleting_a_principal_evicts_readers_of_its_tracked_dependents()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var teaRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(_reader.Orders, tea.Id).SingleOrDefaultAsync(ct)
        );
        _db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;

        _db.Customers.Remove(ada);
        await _db.SaveChangesAsync();

        (await teaRead.WasEvicted()).Should().BeTrue("EF deleted the tracked order with Ada");
    }

    [Fact]
    public async Task With_orphan_deletion_off_Sluice_does_not_make_EF_delete_an_orphan()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        tea.Customer.Should().BeSameAs(ada);
        _db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
        _db.ChangeTracker.DeleteOrphansTiming = CascadeTiming.Never;

        tea.Customer = null;
        var save = () => _db.SaveChangesAsync();

        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*severed*");
        (await _reader.Orders.AnyAsync(o => o.Id == tea.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task With_change_detection_off_an_undetected_edit_is_neither_saved_nor_invalidated()
    {
        var ada = await AddCustomer("Ada");
        var adaRead = await ReadCustomer(ada.Id);
        _db.ChangeTracker.AutoDetectChangesEnabled = false;

        ada.Name = "Undetected";
        await _db.SaveChangesAsync();

        (await _reader.NameOf(ada.Id)).Should().Be("Ada");
        (await adaRead.WasEvicted()).Should().BeFalse();

        _db.ChangeTracker.DetectChanges();
        await _db.SaveChangesAsync();

        (await adaRead.WasEvicted()).Should().BeTrue("the edit was detected and saved");
    }
}
