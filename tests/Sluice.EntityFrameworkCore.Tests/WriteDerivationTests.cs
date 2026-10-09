using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// What a tracked save evicts. Cached reads run on each compute's own context; writes run on a <c>UseSluice</c>
/// context, and the database is checked, and written behind Sluice's back, on one without it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WriteDerivationTests : IAsyncLifetime
{
    private readonly SluiceNode _node = new();
    private readonly PostgresFixture _postgres;
    private readonly ShopDb _reader;
    private readonly ShopDb _db;

    public WriteDerivationTests(PostgresFixture postgres)
    {
        _postgres = postgres;
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

    private async Task<Order> AddOrder(Customer customer, string item, int? couponId = null)
    {
        var order = new Order
        {
            CustomerId = customer.Id,
            Item = item,
            CouponId = couponId,
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    private Task<CachedRead> ReadCustomer(int id) => CachedRead.Customer(_node.Cache, id);

    private Task<CachedRead> ReadOrdersOf(int customerId) =>
        CachedRead.OrdersOf(_node.Cache, customerId);

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

    private async Task<Shipment> AddShipment(Customer customer, int? couponId = null)
    {
        var shipment = new Shipment { CustomerId = customer.Id, CouponId = couponId };
        _db.Shipments.Add(shipment);
        await _db.SaveChangesAsync();
        return shipment;
    }

    private Task<CachedRead> ReadShipmentsOf(int customerId) =>
        CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Shipments, s => s.CustomerId, customerId)
                    .ToListAsync(ct)
        );

    [Fact]
    public async Task With_a_row_version_adding_editing_or_deleting_a_child_evicts_Children_readers_of_its_parent_only()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var adaShipments = await ReadShipmentsOf(ada.Id);
        var bobShipments = await ReadShipmentsOf(bob.Id);

        var parcel = await AddShipment(ada);
        (await adaShipments.WasEvicted()).Should().BeTrue("a shipment was added");
        (await bobShipments.WasEvicted()).Should().BeFalse();

        parcel.Note = "Fragile";
        await _db.SaveChangesAsync();
        (await adaShipments.WasEvicted()).Should().BeTrue("a shipment was edited");
        (await bobShipments.WasEvicted()).Should().BeFalse();

        _db.Shipments.Remove(parcel);
        await _db.SaveChangesAsync();
        (await adaShipments.WasEvicted()).Should().BeTrue("a shipment was deleted");
        (await bobShipments.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task With_a_row_version_re_parenting_a_child_evicts_Children_readers_of_both_parents_only()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var cy = await AddCustomer("Cy");
        var parcel = await AddShipment(ada);
        var adaShipments = await ReadShipmentsOf(ada.Id);
        var bobShipments = await ReadShipmentsOf(bob.Id);
        var cyShipments = await ReadShipmentsOf(cy.Id);

        parcel.CustomerId = bob.Id;
        await _db.SaveChangesAsync();

        (await adaShipments.WasEvicted()).Should().BeTrue("the shipment left Ada");
        (await bobShipments.WasEvicted()).Should().BeTrue("the shipment joined Bob");
        (await cyShipments.WasEvicted()).Should().BeFalse();
    }

    // Without a row version, the parent EF loaded may not be the one the row leaves, so an update or delete evicts the
    // lists under every parent; an insert has no parent before, and its parent after is the one it writes.
    [Fact]
    public async Task Without_a_row_version_editing_re_parenting_or_deleting_a_child_evicts_every_Children_reader_of_its_foreign_key_and_logs_once()
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var cy = await AddCustomer("Cy");
        var adaOrders = await ReadOrdersOf(ada.Id);
        var cyOrders = await ReadOrdersOf(cy.Id);

        var tea = await AddOrder(ada, "Tea");
        (await adaOrders.WasEvicted()).Should().BeTrue("an order was added");
        (await cyOrders.WasEvicted()).Should().BeFalse("an insert evicts its own parent only");

        foreach (
            var (change, save) in new (string, Action)[]
            {
                ("edited", () => tea.Item = "Green tea"),
                ("moved to Bob", () => tea.CustomerId = bob.Id),
                ("deleted", () => _db.Orders.Remove(tea)),
            }
        )
        {
            save();
            await _db.SaveChangesAsync();
            (await cyOrders.WasEvicted())
                .Should()
                .BeTrue(
                    "an order was {0}, and the parent it had may not be the one EF loaded",
                    change
                );
        }

        _node
            .Logs.GetSnapshot()
            .Where(record => record.Id.Id == 3)
            .Select(record => record.Message)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Order's foreign keys")
            .And.Contain("xmin");
    }

    [Fact]
    public async Task A_type_with_a_row_version_or_a_foreign_key_that_is_a_concurrency_token_logs_nothing()
    {
        var ada = await AddCustomer("Ada");
        var parcel = await AddShipment(ada);
        var booking = new Booking { CustomerId = ada.Id };
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();

        parcel.Note = "Fragile";
        booking.Note = "Window seat";
        await _db.SaveChangesAsync();
        _db.RemoveRange(parcel, booking);
        await _db.SaveChangesAsync();

        _node.Logs.GetSnapshot().Should().NotContain(record => record.Id.Id == 3);
    }

    // A row split across tables is written a table at a time, so no token covers it, and the log says so rather than
    // suggesting a row version.
    [Fact]
    public async Task Editing_a_row_split_across_tables_evicts_every_Children_reader_and_logs_why()
    {
        var ada = await AddCustomer("Ada");
        var truck = new Truck
        {
            Plate = "T-3",
            Axles = 2,
            OwnerId = ada.Id,
        };
        _db.Vehicles.Add(truck);
        await _db.SaveChangesAsync();
        var bob = await AddCustomer("Bob");
        var bobTrucks = await Read(
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Set<Truck>(), t => t.OwnerId, bob.Id)
                    .ToListAsync(ct)
        );

        truck.Axles = 3;
        await _db.SaveChangesAsync();

        (await bobTrucks.WasEvicted())
            .Should()
            .BeTrue("the truck's owner may not be the one EF loaded");
        _node
            .Logs.GetSnapshot()
            .Where(record => record.Id.Id == 4)
            .Select(record => record.Message)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Truck is stored across several tables");
    }

    // EF's UPDATE or DELETE matches the row version EF loaded, so a row another writer changed since fails the save,
    // and the parent EF loaded is the one the row had whenever a save succeeds.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_a_row_version_saving_a_row_another_writer_re_parented_since_it_was_loaded_fails(
        bool delete
    )
    {
        var ada = await AddCustomer("Ada");
        var cy = await AddCustomer("Cy");
        var parcel = await AddShipment(ada);
        await using (var other = _postgres.CreateDb(_node))
        {
            (await other.Shipments.SingleAsync(s => s.Id == parcel.Id)).CustomerId = cy.Id;
            await other.SaveChangesAsync();
        }

        if (delete)
        {
            _db.Shipments.Remove(parcel);
        }
        else
        {
            parcel.Note = "Fragile";
        }

        var save = () => _db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await _reader.Shipments.SingleAsync(s => s.Id == parcel.Id))
            .Should()
            .BeEquivalentTo(new { CustomerId = cy.Id, Note = "" }, "the save changed nothing");
    }

    // A change to a row owned in a table of its own writes only that table, so EF doesn't check the owner's row version.
    [Fact]
    public async Task With_a_row_version_changing_an_owned_row_of_an_owner_another_writer_re_parented_evicts_Children_readers_of_its_real_parent()
    {
        var ada = await AddCustomer("Ada");
        var cy = await AddCustomer("Cy");
        var parcel = await AddShipment(ada);
        await using (var other = _postgres.CreateDb(_node))
        {
            (await other.Shipments.SingleAsync(s => s.Id == parcel.Id)).CustomerId = cy.Id;
            await other.SaveChangesAsync();
        }

        var cyShipments = await ReadShipmentsOf(cy.Id);

        parcel.Legs.Add(new Leg { Place = "Depot" });
        await _db.SaveChangesAsync();

        (await _reader.Shipments.SingleAsync(s => s.Id == parcel.Id))
            .Legs.Should()
            .ContainSingle("the save didn't check the shipment's row version");
        (await cyShipments.WasEvicted()).Should().BeTrue("the shipment changed in Cy's list");
    }

    // The foreign key is itself a concurrency token, so EF's UPDATE or DELETE matches the parent it loaded.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_foreign_key_that_is_a_concurrency_token_evicts_Children_readers_of_its_parent_only_and_a_stale_save_fails(
        bool delete
    )
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        Task<CachedRead> ReadBookingsOf(int customerId) =>
            CachedRead.Of(
                _node.Cache,
                (read, ct) =>
                    read.Children(read.Db<ShopDb>().Bookings, b => b.CustomerId, customerId)
                        .ToListAsync(ct)
            );
        var booking = new Booking { CustomerId = ada.Id };
        var stale = new Booking { CustomerId = ada.Id };
        _db.Bookings.AddRange(booking, stale);
        await _db.SaveChangesAsync();
        var adaBookings = await ReadBookingsOf(ada.Id);
        var bobBookings = await ReadBookingsOf(bob.Id);

        void Change(Booking row)
        {
            if (delete)
            {
                _db.Bookings.Remove(row);
            }
            else
            {
                row.Note = "Window seat";
            }
        }

        Change(booking);
        await _db.SaveChangesAsync();
        (await adaBookings.WasEvicted()).Should().BeTrue("Ada's booking changed");
        (await bobBookings.WasEvicted()).Should().BeFalse();

        await _reader
            .Bookings.Where(b => b.Id == stale.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.CustomerId, bob.Id));
        Change(stale);
        var save = () => _db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    // Context A loads the order under Ada; writer B moves it to Bob, and Bob's list is cached again, holding it. A's
    // UPDATE or DELETE matches the row by key alone, so it succeeds, though the parent A loaded is no longer Ada.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_a_row_another_writer_re_parented_since_it_was_loaded_evicts_Children_readers_of_its_real_parent(
        bool delete
    )
    {
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var tea = await AddOrder(ada, "Tea");
        await using (var other = _postgres.CreateDb(_node))
        {
            (await other.Orders.SingleAsync(o => o.Id == tea.Id)).CustomerId = bob.Id;
            await other.SaveChangesAsync();
        }

        var bobOrders = await ReadOrdersOf(bob.Id);
        _db.Entry(tea).Property(o => o.CustomerId).OriginalValue.Should().Be(ada.Id);

        if (delete)
        {
            _db.Orders.Remove(tea);
        }
        else
        {
            tea.Item = "Green tea";
        }

        await _db.SaveChangesAsync();

        (await _reader.Orders.Where(o => o.Id == tea.Id).Select(o => o.Item).ToListAsync())
            .Should()
            .Equal(delete ? [] : ["Green tea"], "EF's save matched the row by its key alone");
        (await bobOrders.WasEvicted()).Should().BeTrue("the order changed in Bob's list");
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
                read.Children(read.Db<ShopDb>().Bins, b => b.WarehouseCode, north.Code)
                    .ToListAsync(ct)
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
                            read.Children(read.Db<ShopDb>().Orders, o => o.Customer, parent)
                                .ToListAsync(ct)
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
                                    read.Db<ShopDb>().Notes,
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

    // With a row version, so the save is exact: a null key has no parent before or after.
    [Fact]
    public async Task Setting_a_nullable_foreign_key_to_or_from_null_evicts_Children_readers_of_its_principal()
    {
        var ada = await AddCustomer("Ada");
        var coupon = await AddCoupon();
        var other = await AddCoupon();
        var parcel = await AddShipment(ada);
        Task<CachedRead> ReadShipmentsWith(int couponId) =>
            CachedRead.Of(
                _node.Cache,
                (read, ct) =>
                    read.Children(read.Db<ShopDb>().Shipments, s => s.CouponId, couponId)
                        .ToListAsync(ct)
            );
        var couponShipments = await ReadShipmentsWith(coupon.Id);
        var otherShipments = await ReadShipmentsWith(other.Id);

        parcel.CouponId = coupon.Id;
        await _db.SaveChangesAsync();
        (await couponShipments.WasEvicted()).Should().BeTrue("the shipment gained the coupon");
        (await otherShipments.WasEvicted()).Should().BeFalse();

        parcel.CouponId = null;
        await _db.SaveChangesAsync();
        (await couponShipments.WasEvicted()).Should().BeTrue("the shipment lost the coupon");
        (await otherShipments.WasEvicted()).Should().BeFalse();
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
                    read.Children(read.Db<ShopDb>().Bins, b => b.WarehouseCode, code)
                        .ToListAsync(ct)
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

    // The database cascades by the code the warehouse holds when the delete runs, not the one EF loaded: unlike a primary
    // key, an alternate key can change.
    [Fact]
    public async Task Deleting_a_principal_whose_alternate_key_another_writer_changed_evicts_Children_readers_of_the_key_it_held()
    {
        var renamed = $"M-{Guid.NewGuid():N}";
        var north = new Warehouse { Code = $"N-{Guid.NewGuid():N}" };
        _db.Warehouses.Add(north);
        await _db.SaveChangesAsync();
        await _reader.Database.ExecuteSqlAsync(
            $"""update "Warehouses" set "Code" = {renamed} where "Id" = {north.Id}"""
        );
        _reader.Bins.Add(new Bin { WarehouseCode = renamed });
        await _reader.SaveChangesAsync();
        var renamedBins = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Bins, b => b.WarehouseCode, renamed).ToListAsync(ct)
        );

        _db.Warehouses.Remove(north);
        await _db.SaveChangesAsync();

        (await _reader.Bins.AnyAsync(b => b.WarehouseCode == renamed))
            .Should()
            .BeFalse("the database deleted the bin with its warehouse");
        (await renamedBins.WasEvicted()).Should().BeTrue();
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
                    read.Children(read.Db<ShopDb>().CustomerCoupons, foreignKey, key)
                        .ToListAsync(ct)
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
            (read, ct) => read.Entity(read.Db<ShopDb>().Parcels, parcel.Id).SingleAsync(ct)
        );
        var adaParcels = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Parcels, p => p.CustomerId, ada.Id).ToListAsync(ct)
        );
        var otherRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Parcels, other.Id).SingleAsync(ct)
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
    public async Task Editing_a_derived_row_evicts_readers_of_its_root_type_with_TPH_TPT_or_TPC()
    {
        var rex = new Dog { Name = "Rex" };
        var truck = new Truck { Plate = "AB12 CDE", Axles = 2 };
        var circle = new Circle { Radius = 1 };
        _db.AddRange(rex, truck, circle);
        await _db.SaveChangesAsync();
        var rexRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Animals, rex.Id).SingleAsync(ct)
        );
        var animals = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Db<ShopDb>().Animals.ToListAsync(ct)
        );
        var truckRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Vehicles, truck.Id).SingleAsync(ct)
        );
        var vehicles = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Db<ShopDb>().Vehicles.ToListAsync(ct)
        );
        var circleRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Shapes, circle.Id).SingleAsync(ct)
        );
        var shapes = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Db<ShopDb>().Shapes.ToListAsync(ct)
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

        circle.Radius = 2;
        await _db.SaveChangesAsync();
        (await circleRead.WasEvicted())
            .Should()
            .BeTrue("a column in the circle's own table changed");
        (await shapes.WasEvicted()).Should().BeTrue("a column in the circle's own table changed");
        (await vehicles.WasEvicted()).Should().BeFalse();
    }

    [Fact]
    public async Task Any_write_to_a_type_evicts_its_All_readers_and_a_write_to_another_type_does_not()
    {
        var all = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Db<ShopDb>().Customers.ToListAsync(ct)
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
            (read, ct) => read.Entity(read.Db<ShopDb>().Users, graceId).SingleAsync(ct)
        );
        var alanRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Users, alanId).SingleAsync(ct)
        );
        var teaRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Products, new Sku(sku.ToUpperInvariant()))
                    .SingleAsync(ct)
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
            (read, ct) =>
                read.Children(read.Db<ShopDb>().OrderLines, l => l.OrderId, tea.Id).ToListAsync(ct)
        );
        var coffeeLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().OrderLines, l => l.OrderId, coffee.Id)
                    .ToListAsync(ct)
        );
        var allLines = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Db<ShopDb>().OrderLines.ToListAsync(ct)
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
                read.Children(read.Db<ShopDb>().OrderLines, l => l.CouponId, coupon.Id)
                    .ToListAsync(ct)
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

    private async Task<Coupon> AddCoupon()
    {
        var coupon = new Coupon();
        _db.Coupons.Add(coupon);
        await _db.SaveChangesAsync();
        return coupon;
    }

    private async Task<Note> AddNote(Customer customer, Note? replyTo = null)
    {
        var note = new Note { ReplyToId = replyTo?.Id };
        _db.Notes.Add(note);
        _db.Entry(note).Property("CustomerId").CurrentValue = customer.Id;
        await _db.SaveChangesAsync();
        return note;
    }

    private Task<CachedRead> Read<T>(Func<SluiceReads, CancellationToken, Task<T>> read) =>
        CachedRead.Of(_node.Cache, read);

    [Fact]
    public async Task Deleting_a_principal_evicts_every_reader_of_the_rows_the_database_cascades_into_at_any_depth()
    {
        var ada = await AddCustomer("Ada");
        var coupon = await AddCoupon();
        var tea = await AddOrder(ada, "Tea", coupon.Id);
        _db.OrderLines.Add(new OrderLine { OrderId = tea.Id, LineNumber = 1 });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        (string Reader, CachedRead Read)[] readers =
        [
            (
                "the order",
                await Read(
                    (read, ct) =>
                        read.Entity(read.Db<ShopDb>().Orders, tea.Id).SingleOrDefaultAsync(ct)
                )
            ),
            ("every order", await Read((read, ct) => read.Db<ShopDb>().Orders.ToListAsync(ct))),
            (
                "the coupon's orders",
                await Read(
                    (read, ct) =>
                        read.Children(read.Db<ShopDb>().Orders, o => o.CouponId, coupon.Id)
                            .ToListAsync(ct)
                )
            ),
            (
                "every order line",
                await Read((read, ct) => read.Db<ShopDb>().OrderLines.ToListAsync(ct))
            ),
            (
                "the order's lines",
                await Read(
                    (read, ct) =>
                        read.Children(read.Db<ShopDb>().OrderLines, l => l.OrderId, tea.Id)
                            .ToListAsync(ct)
                )
            ),
        ];

        // Only the customer is tracked, so the database deletes the order, and the order's line in turn.
        _db.Customers.Remove(await _db.Customers.SingleAsync(c => c.Id == ada.Id));
        await _db.SaveChangesAsync();

        (await _reader.OrderLines.AnyAsync(l => l.OrderId == tea.Id)).Should().BeFalse();
        foreach (var (reader, read) in readers)
        {
            (await read.WasEvicted()).Should().BeTrue("the database deleted rows {0} read", reader);
        }
    }

    [Fact]
    public async Task Deleting_a_principal_evicts_every_reader_of_the_rows_the_database_sets_to_null()
    {
        var ada = await AddCustomer("Ada");
        var coupon = await AddCoupon();
        var tea = await AddOrder(ada, "Tea", coupon.Id);
        _db.ChangeTracker.Clear();
        var teaRead = await Read(
            (read, ct) => read.Entity(read.Db<ShopDb>().Orders, tea.Id).SingleAsync(ct)
        );
        var orders = await Read((read, ct) => read.Db<ShopDb>().Orders.ToListAsync(ct));
        var adasOrders = await ReadOrdersOf(ada.Id);
        var adaRead = await ReadCustomer(ada.Id);

        _db.Coupons.Remove(await _db.Coupons.SingleAsync(c => c.Id == coupon.Id));
        await _db.SaveChangesAsync();

        (await _reader.Orders.SingleAsync(o => o.Id == tea.Id)).CouponId.Should().BeNull();
        (await teaRead.WasEvicted()).Should().BeTrue("the database set the order's coupon to null");
        (await orders.WasEvicted()).Should().BeTrue("the database set the order's coupon to null");
        (await adasOrders.WasEvicted())
            .Should()
            .BeTrue("the order changed in its customer's list too");
        (await adaRead.WasEvicted()).Should().BeFalse("no customer changed");
    }

    [Fact]
    public async Task Deleting_a_principal_evicts_nothing_more_for_dependents_the_database_leaves_alone()
    {
        _db.Model.FindEntityType(typeof(Dog))!
            .FindNavigation(nameof(Dog.Owner))!
            .ForeignKey.DeleteBehavior.Should()
            .Be(DeleteBehavior.ClientSetNull, "the database takes no action on a dog's owner");
        var ada = await AddCustomer("Ada");
        var bob = await AddCustomer("Bob");
        var rex = new Dog { Name = "Rex", OwnerId = bob.Id };
        _db.Animals.Add(rex);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var rexRead = await Read(
            (read, ct) => read.Entity(read.Db<ShopDb>().Animals, rex.Id).SingleAsync(ct)
        );
        var animals = await Read(
            (read, ct) => read.Db<ShopDb>().Animals.IgnoreAutoIncludes().ToListAsync(ct)
        );

        _db.Customers.Remove(await _db.Customers.SingleAsync(c => c.Id == ada.Id));
        await _db.SaveChangesAsync();

        (await rexRead.WasEvicted()).Should().BeFalse("no dog changed");
        (await animals.WasEvicted()).Should().BeFalse("no dog changed");
    }

    [Fact]
    public async Task A_cascade_from_a_type_to_itself_evicts_readers_at_every_depth_and_ends()
    {
        var ada = await AddCustomer("Ada");
        var question = await AddNote(ada);
        var answer = await AddNote(ada, replyTo: question);
        var thanks = await AddNote(ada, replyTo: answer);
        _db.ChangeTracker.Clear();
        (string Reader, CachedRead Read)[] readers =
        [
            (
                "the answer",
                await Read(
                    (read, ct) =>
                        read.Entity(read.Db<ShopDb>().Notes, answer.Id).SingleOrDefaultAsync(ct)
                )
            ),
            (
                "the thanks",
                await Read(
                    (read, ct) =>
                        read.Entity(read.Db<ShopDb>().Notes, thanks.Id).SingleOrDefaultAsync(ct)
                )
            ),
            ("every note", await Read((read, ct) => read.Db<ShopDb>().Notes.ToListAsync(ct))),
            (
                "the replies to the answer",
                await Read(
                    (read, ct) =>
                        read.Children(read.Db<ShopDb>().Notes, n => n.ReplyToId, answer.Id)
                            .ToListAsync(ct)
                )
            ),
        ];

        // Only the question is tracked, so the database deletes the answer, and the thanks in turn.
        _db.Notes.Remove(await _db.Notes.SingleAsync(n => n.Id == question.Id));
        await _db.SaveChangesAsync();

        (await _reader.Notes.AnyAsync(n => n.Id == thanks.Id)).Should().BeFalse();
        foreach (var (reader, read) in readers)
        {
            (await read.WasEvicted()).Should().BeTrue("the database deleted rows {0} read", reader);
        }
    }

    [Fact]
    public async Task AllDependencies_of_a_principal_set_evicts_readers_of_the_rows_an_ExecuteDelete_cascades_into()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        _db.OrderLines.Add(new OrderLine { OrderId = tea.Id, LineNumber = 1 });
        await _db.SaveChangesAsync();
        (string Reader, CachedRead Read)[] readers =
        [
            (
                "the order",
                await Read(
                    (read, ct) =>
                        read.Entity(read.Db<ShopDb>().Orders, tea.Id).SingleOrDefaultAsync(ct)
                )
            ),
            ("every order", await Read((read, ct) => read.Db<ShopDb>().Orders.ToListAsync(ct))),
            ("the customer's orders", await ReadOrdersOf(ada.Id)),
            (
                "every order line",
                await Read((read, ct) => read.Db<ShopDb>().OrderLines.ToListAsync(ct))
            ),
            (
                "the order's lines",
                await Read(
                    (read, ct) =>
                        read.Children(read.Db<ShopDb>().OrderLines, l => l.OrderId, tea.Id)
                            .ToListAsync(ct)
                )
            ),
        ];

        // On a context without Sluice, so only the invalidation by hand can evict.
        await _reader.Customers.Where(c => c.Id == ada.Id).ExecuteDeleteAsync();
        await _node.Cache.InvalidateAsync(_db.Customers.AllDependencies());

        (await _reader.OrderLines.AnyAsync(l => l.OrderId == tea.Id)).Should().BeFalse();
        foreach (var (reader, read) in readers)
        {
            (await read.WasEvicted()).Should().BeTrue("the database deleted rows {0} read", reader);
        }
    }

    [Fact]
    public async Task Deleting_a_row_evicts_no_reader_of_another_row_of_its_type()
    {
        var north = new Warehouse { Code = $"N-{Guid.NewGuid():N}" };
        var bin = new Bin { WarehouseCode = north.Code };
        var otherBin = new Bin { WarehouseCode = north.Code };
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var coffee = await AddOrder(ada, "Coffee");
        _db.AddRange(north, bin, otherBin);
        await _db.SaveChangesAsync();
        var otherBinRead = await Read(
            (read, ct) => read.Entity(read.Db<ShopDb>().Bins, otherBin.Id).SingleAsync(ct)
        );
        var coffeeRead = await Read(
            (read, ct) => read.Entity(read.Db<ShopDb>().Orders, coffee.Id).SingleAsync(ct)
        );
        var adaRead = await ReadCustomer(ada.Id);

        _db.Bins.Remove(bin);
        await _db.SaveChangesAsync();
        (await otherBinRead.WasEvicted()).Should().BeFalse("nothing cascades from a bin");

        // Table-per-type links a truck's row to its vehicle row, which is the same row, not a cascade.
        var truck = new Truck { Plate = "T-2", Axles = 2 };
        var otherTruck = new Truck { Plate = "T-3", Axles = 2 };
        _db.Vehicles.AddRange(truck, otherTruck);
        await _db.SaveChangesAsync();
        var otherTruckRead = await Read(
            (read, ct) => read.Entity(read.Db<ShopDb>().Vehicles, otherTruck.Id).SingleAsync(ct)
        );
        _db.Vehicles.Remove(truck);
        await _db.SaveChangesAsync();
        (await otherTruckRead.WasEvicted()).Should().BeFalse("nothing cascades from a truck");

        // The database deletes the order's lines, but no other order.
        _db.Orders.Remove(tea);
        await _db.SaveChangesAsync();
        (await coffeeRead.WasEvicted()).Should().BeFalse("only the deleted order's lines cascade");
        (await adaRead.WasEvicted()).Should().BeFalse("only the deleted order's lines cascade");
    }

    [Fact]
    public async Task Deleting_a_derived_row_through_a_base_type_stub_evicts_lists_through_foreign_keys_into_the_derived_type()
    {
        var truck = new Truck { Plate = "T-1", Axles = 3 };
        _db.Vehicles.Add(truck);
        await _db.SaveChangesAsync();
        _db.Trailers.Add(new Trailer { TruckId = truck.Id });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var trailers = await Read(
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Trailers, t => t.TruckId, truck.Id).ToListAsync(ct)
        );

        // The context only knows the row as a vehicle; the database deletes the truck's trailer with it.
        _db.Vehicles.Remove(new Vehicle { Id = truck.Id });
        await _db.SaveChangesAsync();

        (await _reader.Trailers.AnyAsync(t => t.TruckId == truck.Id)).Should().BeFalse();
        (await trailers.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_derived_row_through_a_base_type_stub_evicts_lists_through_the_derived_type_s_own_foreign_keys()
    {
        var ada = await AddCustomer("Ada");
        var truck = new Truck
        {
            Plate = "T-2",
            Axles = 2,
            OwnerId = ada.Id,
        };
        _db.Vehicles.Add(truck);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var trucks = await Read(
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Set<Truck>(), t => t.OwnerId, ada.Id)
                    .ToListAsync(ct)
        );

        // The context only knows the row as a vehicle, which has no owner.
        _db.Vehicles.Remove(new Vehicle { Id = truck.Id });
        await _db.SaveChangesAsync();

        (await trucks.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task With_cascades_on_save_deleting_a_principal_evicts_readers_of_its_tracked_dependents()
    {
        var ada = await AddCustomer("Ada");
        var tea = await AddOrder(ada, "Tea");
        var teaRead = await CachedRead.Of(
            _node.Cache,
            (read, ct) => read.Entity(read.Db<ShopDb>().Orders, tea.Id).SingleOrDefaultAsync(ct)
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

        await save.Should().ThrowAsync<InvalidOperationException>();
        (await _reader.Orders.AnyAsync(o => o.Id == tea.Id))
            .Should()
            .BeTrue("the orphan wasn't deleted");
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

    // A token in one table doesn't guard the columns of another: a table-per-type row is split across two.
    [Fact]
    public void Only_a_row_stored_in_one_table_with_a_token_the_database_changes_on_every_write_has_a_row_version()
    {
        using var vessels = new VesselDb(
            new DbContextOptionsBuilder<VesselDb>().UseNpgsql("Host=unused").Options
        );
        bool HasRowVersion(DbContext db, Type type) =>
            ModelSources.HasRowVersion(db.Model.FindEntityType(type)!);

        HasRowVersion(vessels, typeof(Vessel)).Should().BeTrue();
        HasRowVersion(vessels, typeof(Tanker))
            .Should()
            .BeFalse("its own columns are in a table its row version isn't");
        HasRowVersion(_db, typeof(Shipment)).Should().BeTrue();
        HasRowVersion(_db, typeof(Booking)).Should().BeFalse("only the app changes its token");
        HasRowVersion(_db, typeof(Order)).Should().BeFalse("it has no token");
    }

    private class Vessel
    {
        public int Id { get; set; }
        public uint Version { get; set; }
    }

    private sealed class Tanker : Vessel
    {
        public int Tanks { get; set; }
    }

    private sealed class VesselDb(DbContextOptions<VesselDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Vessel>().UseTptMappingStrategy();
            model.Entity<Vessel>().Property(vessel => vessel.Version).IsRowVersion();
            model.Entity<Tanker>();
        }
    }
}
