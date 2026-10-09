using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// The highest key of each table a random workload writes. Taken before the workload starts, the workload's rows are
/// the ones above it: tests in the Postgres collection run one at a time, so nothing else adds rows meanwhile.
/// </summary>
public sealed record Keys(
    int Customer,
    int Order,
    int Coupon,
    int Note,
    int Vehicle,
    int Trailer,
    int Shipment
)
{
    public static async Task<Keys> Of(ShopDb db) =>
        new(
            await db.Customers.MaxAsync(row => (int?)row.Id) ?? 0,
            await db.Orders.MaxAsync(row => (int?)row.Id) ?? 0,
            await db.Coupons.MaxAsync(row => (int?)row.Id) ?? 0,
            await db.Notes.MaxAsync(row => (int?)row.Id) ?? 0,
            await db.Vehicles.MaxAsync(row => (int?)row.Id) ?? 0,
            await db.Trailers.MaxAsync(row => (int?)row.Id) ?? 0,
            await db.Shipments.MaxAsync(row => (int?)row.Id) ?? 0
        );

    /// <summary>Each table's higher key, so keys a workload has used stay covered after their rows are deleted.</summary>
    public Keys Max(Keys other) =>
        new(
            Math.Max(Customer, other.Customer),
            Math.Max(Order, other.Order),
            Math.Max(Coupon, other.Coupon),
            Math.Max(Note, other.Note),
            Math.Max(Vehicle, other.Vehicle),
            Math.Max(Trailer, other.Trailer),
            Math.Max(Shipment, other.Shipment)
        );
}

/// <summary>A read of the shop on the compute's own context, rendered as text, and named so a mismatch says which.</summary>
public sealed record ShopView(
    string Name,
    Func<ShopDb, SluiceReads, CancellationToken, Task<string>> Read
)
{
    public Task<string> Run(SluiceReads read, CancellationToken ct) =>
        Read(read.Db<ShopDb>(), read, ct);
}

public static class ShopViews
{
    /// <summary>
    /// Each kind of read of the workload's rows: every <c>Entity</c> and <c>Children</c> read of each key from
    /// <paramref name="first"/> (exclusive) to <paramref name="last"/>, deleted keys included, plain queries of each
    /// key, and plain queries over the workload's rows that follow navigations, many-to-many navigations, joins,
    /// subqueries and <c>Include</c> into other types.
    /// </summary>
    public static IReadOnlyList<ShopView> Of(Keys first, Keys last)
    {
        var views = new List<ShopView>
        {
            new(
                "customers",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Customers.Where(c => c.Id > first.Customer)
                            .OrderBy(c => c.Id)
                            .Select(c => new { c.Id, c.Name })
                            .ToListAsync(ct)
                    )
            ),
            // A filter over a column a write can change: an order that becomes big joins it.
            new(
                "big orders",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Orders.Where(o => o.Id > first.Order && o.Item == "big")
                            .OrderBy(o => o.Id)
                            .Select(o => o.Id)
                            .ToListAsync(ct)
                    )
            ),
            new(
                "order lines",
                async (db, read, ct) =>
                    Text(
                        await db
                            .OrderLines.Where(l => l.OrderId > first.Order)
                            .OrderBy(l => l.OrderId)
                            .ThenBy(l => l.LineNumber)
                            .Select(l => new
                            {
                                l.OrderId,
                                l.LineNumber,
                                l.CouponId,
                            })
                            .ToListAsync(ct)
                    )
            ),
            new(
                "trucks",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Vehicles.Where(v => v.Id > first.Vehicle)
                            .OfType<Truck>()
                            .OrderBy(t => t.Id)
                            .Select(t => new { t.Id, t.Axles })
                            .ToListAsync(ct)
                    )
            ),
            new(
                "trailers",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Trailers.Where(t => t.Id > first.Trailer)
                            .OrderBy(t => t.Id)
                            .Select(t => new { t.Id, t.TruckId })
                            .ToListAsync(ct)
                    )
            ),
            new(
                "orders with their customer's name",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Orders.Where(o => o.Id > first.Order)
                            .OrderBy(o => o.Id)
                            .Select(o => new { o.Id, o.Customer!.Name })
                            .ToListAsync(ct)
                    )
            ),
            new(
                "orders with their customer, included",
                async (db, read, ct) =>
                    Text(
                        (
                            await db
                                .Orders.Include(o => o.Customer)
                                .Where(o => o.Id > first.Order)
                                .OrderBy(o => o.Id)
                                .ToListAsync(ct)
                        ).Select(o => $"{o.Id} {o.Item} {o.Customer!.Name}")
                    )
            ),
            new(
                "customers with their order count",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Customers.Where(c => c.Id > first.Customer)
                            .OrderBy(c => c.Id)
                            .Select(c => new
                            {
                                c.Id,
                                Orders = db.Orders.Count(o => o.CustomerId == c.Id),
                            })
                            .ToListAsync(ct)
                    )
            ),
            new(
                "customers with their coupons",
                async (db, read, ct) =>
                    Text(
                        (
                            await db
                                .Customers.Where(c => c.Id > first.Customer)
                                .OrderBy(c => c.Id)
                                .Select(c => new
                                {
                                    c.Id,
                                    Coupons = c
                                        .Coupons.OrderBy(x => x.Id)
                                        .Select(x => x.Id)
                                        .ToList(),
                                })
                                .ToListAsync(ct)
                        ).Select(c => $"{c.Id}: {string.Join(",", c.Coupons)}")
                    )
            ),
            new(
                "order lines joined to their order",
                async (db, read, ct) =>
                    Text(
                        await (
                            from o in db.Orders
                            join l in db.OrderLines on o.Id equals l.OrderId
                            where o.Id > first.Order
                            orderby o.Id, l.LineNumber
                            select new
                            {
                                o.Id,
                                o.CustomerId,
                                l.LineNumber,
                                l.CouponId,
                            }
                        ).ToListAsync(ct)
                    )
            ),
            new(
                "notes with their reply count",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Notes.Where(n => n.Id > first.Note)
                            .OrderBy(n => n.Id)
                            .Select(n => new
                            {
                                n.Id,
                                Replies = db.Notes.Count(r => r.ReplyToId == n.Id),
                            })
                            .ToListAsync(ct)
                    )
            ),
            new(
                "trucks with their trailer count",
                async (db, read, ct) =>
                    Text(
                        await db
                            .Vehicles.OfType<Truck>()
                            .Where(t => t.Id > first.Vehicle)
                            .OrderBy(t => t.Id)
                            .Select(t => new
                            {
                                t.Id,
                                t.Axles,
                                Trailers = db.Trailers.Count(x => x.TruckId == t.Id),
                            })
                            .ToListAsync(ct)
                    )
            ),
        };

        foreach (var id in Between(first.Customer, last.Customer))
        {
            views.Add(
                new(
                    $"customer {id}, found",
                    async (db, read, ct) => (await db.Customers.FindAsync([id], ct))?.Name ?? "none"
                )
            );
            views.Add(
                new(
                    $"orders of customer {id}, filtered",
                    async (db, read, ct) =>
                        Text(
                            await db
                                .Orders.Where(o => o.CustomerId == id)
                                .OrderBy(o => o.Id)
                                .Select(o => o.Item)
                                .ToListAsync(ct)
                        )
                )
            );
            views.Add(
                new(
                    $"customer {id}",
                    async (db, read, ct) =>
                        await read.Entity(db.Customers, id)
                            .Select(c => c.Name)
                            .SingleOrDefaultAsync(ct)
                        ?? "none"
                )
            );
            views.Add(
                new(
                    $"orders of customer {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.Orders, o => o.CustomerId, id)
                                .OrderBy(o => o.Id)
                                .Select(o => new
                                {
                                    o.Id,
                                    o.Item,
                                    o.CouponId,
                                })
                                .ToListAsync(ct)
                        )
                )
            );
            views.Add(
                new(
                    $"order count of customer {id}",
                    async (db, read, ct) =>
                        Number(await read.Children(db.Orders, o => o.Customer, id).CountAsync(ct))
                )
            );
            views.Add(
                new(
                    $"notes of customer {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(
                                    db.Notes,
                                    n => EF.Property<int>(n, "CustomerId"),
                                    id
                                )
                                .OrderBy(n => n.Id)
                                .Select(n => n.Id)
                                .ToListAsync(ct)
                        )
                )
            );
            views.Add(
                new(
                    $"shipments of customer {id}",
                    async (db, read, ct) =>
                        Text(
                            (
                                await read.Children(db.Shipments, s => s.CustomerId, id)
                                    .OrderBy(s => s.Id)
                                    .ToListAsync(ct)
                            ).Select(s =>
                                $"{s.Id} {s.Note} {s.CouponId} {string.Join(",", s.Legs.Select(l => l.Place))}"
                            )
                        )
                )
            );
            views.Add(
                new(
                    $"coupons of customer {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(
                                    db.CustomerCoupons,
                                    l => EF.Property<int>(l, "CustomerId"),
                                    id
                                )
                                .Select(l => EF.Property<int>(l, "CouponId"))
                                .OrderBy(coupon => coupon)
                                .ToListAsync(ct)
                        )
                )
            );
        }

        foreach (var id in Between(first.Order, last.Order))
        {
            views.Add(
                new(
                    $"order {id} with its customer's name",
                    async (db, read, ct) =>
                        (
                            await read.Entity(db.Orders, id)
                                .Select(o => new { o.Item, o.Customer!.Name })
                                .SingleOrDefaultAsync(ct)
                        )?.ToString() ?? "none"
                )
            );
            views.Add(
                new(
                    $"order {id}",
                    async (db, read, ct) =>
                        (
                            await read.Entity(db.Orders, id)
                                .Select(o => new
                                {
                                    o.CustomerId,
                                    o.Item,
                                    o.CouponId,
                                })
                                .SingleOrDefaultAsync(ct)
                        )?.ToString() ?? "none"
                )
            );
            views.Add(
                new(
                    $"lines of order {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.OrderLines, l => l.OrderId, id)
                                .OrderBy(l => l.LineNumber)
                                .Select(l => new { l.LineNumber, l.CouponId })
                                .ToListAsync(ct)
                        )
                )
            );
        }

        foreach (var id in Between(first.Coupon, last.Coupon))
        {
            views.Add(
                new(
                    $"coupon {id}",
                    async (db, read, ct) =>
                        await read.Entity(db.Coupons, id).AnyAsync(ct) ? "exists" : "none"
                )
            );
            views.Add(
                new(
                    $"orders with coupon {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.Orders, o => o.CouponId, id)
                                .OrderBy(o => o.Id)
                                .Select(o => o.Id)
                                .ToListAsync(ct)
                        )
                )
            );
            views.Add(
                new(
                    $"shipments with coupon {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.Shipments, s => s.CouponId, id)
                                .OrderBy(s => s.Id)
                                .Select(s => new { s.Id, s.CustomerId })
                                .ToListAsync(ct)
                        )
                )
            );
            views.Add(
                new(
                    $"lines with coupon {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.OrderLines, l => l.CouponId, id)
                                .OrderBy(l => l.OrderId)
                                .ThenBy(l => l.LineNumber)
                                .Select(l => new { l.OrderId, l.LineNumber })
                                .ToListAsync(ct)
                        )
                )
            );
            views.Add(
                new(
                    $"customers with coupon {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(
                                    db.CustomerCoupons,
                                    l => EF.Property<int>(l, "CouponId"),
                                    id
                                )
                                .Select(l => EF.Property<int>(l, "CustomerId"))
                                .OrderBy(customer => customer)
                                .ToListAsync(ct)
                        )
                )
            );
        }

        foreach (var id in Between(first.Note, last.Note))
        {
            views.Add(
                new(
                    $"note {id}",
                    async (db, read, ct) =>
                        (
                            await read.Entity(db.Notes, id)
                                .Select(n => new
                                {
                                    n.Text,
                                    n.ReplyToId,
                                    CustomerId = EF.Property<int?>(n, "CustomerId"),
                                })
                                .SingleOrDefaultAsync(ct)
                        )?.ToString() ?? "none"
                )
            );
            views.Add(
                new(
                    $"replies to note {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.Notes, n => n.ReplyToId, id)
                                .OrderBy(n => n.Id)
                                .Select(n => n.Id)
                                .ToListAsync(ct)
                        )
                )
            );
        }

        foreach (var id in Between(first.Vehicle, last.Vehicle))
        {
            views.Add(
                new(
                    $"vehicle {id}",
                    async (db, read, ct) =>
                        await read.Entity(db.Vehicles, id).SingleOrDefaultAsync(ct) switch
                        {
                            null => "none",
                            Truck truck => $"truck {truck.Plate} {truck.Axles}",
                            var vehicle => $"vehicle {vehicle.Plate}",
                        }
                )
            );
            views.Add(
                new(
                    $"trailers of truck {id}",
                    async (db, read, ct) =>
                        Text(
                            await read.Children(db.Trailers, t => t.TruckId, id)
                                .OrderBy(t => t.Id)
                                .Select(t => t.Id)
                                .ToListAsync(ct)
                        )
                )
            );
        }

        foreach (var id in Between(first.Shipment, last.Shipment))
        {
            views.Add(
                new(
                    $"shipment {id}",
                    async (db, read, ct) =>
                        await read.Entity(db.Shipments, id).SingleOrDefaultAsync(ct) is { } s
                            ? $"{s.CustomerId} {s.Note} {s.CouponId} {s.Legs.Count}"
                            : "none"
                )
            );
        }

        return views;
    }

    private static IEnumerable<int> Between(int first, int last) =>
        Enumerable.Range(first + 1, Math.Max(0, last - first));

    private static string Text<T>(IEnumerable<T> rows) => $"[{string.Join("; ", rows)}]";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Makes random changes to the shop's rows through a context with <c>UseSluice</c>, the ways an app would: tracked
/// inserts, edits and deletes, deletes of rows it never loaded, <c>Update</c> of a detached row, several changes in
/// one save, saves in a transaction that commits or rolls back, and bulk statements that edit, re-parent and delete
/// rows, alone or in a transaction. Shipments have a row version, so concurrent writers' saves of the same one fail
/// rather than overwrite each other, and Sluice evicts their parents' lists exactly. It reads the rows it may pick from the database before each change, through the
/// same context, so it sees its own uncommitted rows inside a transaction.
/// </summary>
public sealed class RandomWriter(ShopDb db, Keys first, Random random)
{
    private static readonly string[] _names = ["Ada", "Bob", "Cy", "Di", "Ed"];
    private static readonly string[] _plates = ["AB1", "CD2", "EF3"];

    /// <summary>Makes one random change, and describes it. A change the database refused says so.</summary>
    public async Task<string> Step()
    {
        try
        {
            return random.Next(10) switch
            {
                0 => await Batch(),
                1 => await InTransaction(),
                _ => await Saved(await Stage(await State.Of(db, first), bulk: true)),
            };
        }
        // Concurrent writers pick rows another writer has just deleted or changed, and deadlock each other, which
        // Npgsql's execution strategy wraps as transient. Staged changes picked from one state can clash with each
        // other. A refused save still invalidates what it was saving.
        catch (Exception exception)
            when (IsFrom<DbUpdateException>(exception) || IsFrom<PostgresException>(exception))
        {
            return $"refused: {exception.GetType().Name}";
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("contains no elements", StringComparison.Ordinal))
        {
            return "refused: the row it picked was gone";
        }
        // EF's own refusal of some mixes of concurrent changes in one save; it throws the same with Sluice left out.
        catch (ArgumentOutOfRangeException exception)
            when (exception.Message.Contains(
                    "Unexpected entry.EntityState",
                    StringComparison.Ordinal
                )
            )
        {
            return "refused: EF rejected the mix of changes";
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<string> Saved(string change)
    {
        await db.SaveChangesAsync();
        return change;
    }

    private async Task<string> Batch()
    {
        var state = await State.Of(db, first);
        var changes = new List<string>();
        var count = random.Next(2, 4);
        for (var i = 0; i < count; i++)
        {
            // Changes picked from one state can stage the same new row twice, which EF refuses before saving.
            try
            {
                changes.Add(await Stage(state, bulk: false));
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("already being tracked", StringComparison.Ordinal))
            {
                changes.Add("a clashing change, skipped");
            }
        }

        return await Saved($"in one save: {string.Join(", ", changes)}");
    }

    private async Task<string> InTransaction()
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var changes = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            changes.Add(await Saved(await Stage(await State.Of(db, first), bulk: true)));
            db.ChangeTracker.Clear();
        }

        if (random.Next(2) == 0)
        {
            await transaction.RollbackAsync();
            return $"rolled back: {string.Join(", ", changes)}";
        }

        await transaction.CommitAsync();
        return $"committed: {string.Join(", ", changes)}";
    }

    // Stages one change for the next save, or, with bulk, may run a bulk statement at once, which nobody invalidates by
    // hand. A batch passes no bulk: its changes are all picked from one state, which a bulk statement could change
    // under the others.
    private async Task<string> Stage(State state, bool bulk)
    {
        var changes = new List<(int Weight, Func<Task<string>> Make)>();
        void Offer(int weight, bool possible, Func<Task<string>> make)
        {
            if (possible)
            {
                changes.Add((weight, make));
            }
        }

        // The state is read one table at a time, so under concurrent writers a change that also picks a customer checks
        // there is one: orders or notes can appear after the customers were read.
        var any = (int[] rows) => rows.Length > 0;
        Offer(state.Customers.Length < 6 ? 3 : 0, true, AddCustomer);
        Offer(2, any(state.Customers), () => RenameCustomer(Pick(state.Customers)));
        Offer(1, any(state.Customers), () => DeleteCustomer(Pick(state.Customers)));
        Offer(1, any(state.Customers), () => DeleteCustomerUnloaded(Pick(state.Customers)));
        Offer(4, any(state.Customers), () => AddOrder(state));
        Offer(
            4,
            any(state.Orders) && any(state.Customers),
            () => EditOrder(state, Pick(state.Orders))
        );
        Offer(
            2,
            any(state.Orders) && any(state.Customers),
            () => UpdateDetachedOrder(state, Pick(state.Orders))
        );
        Offer(1, any(state.Orders), () => DeleteOrder(Pick(state.Orders)));
        Offer(1, any(state.Orders), () => DeleteOrderUnloaded(Pick(state.Orders)));
        Offer(3, any(state.Orders), () => AddLine(state, Pick(state.Orders)));
        Offer(2, state.Lines.Length > 0, () => EditLine(state, Pick(state.Lines)));
        Offer(1, state.Lines.Length > 0, () => DeleteLine(Pick(state.Lines)));
        Offer(state.Coupons.Length < 4 ? 2 : 0, true, AddCoupon);
        Offer(1, any(state.Coupons), () => DeleteCoupon(Pick(state.Coupons)));
        Offer(2, any(state.Customers) && any(state.Coupons), () => Link(state));
        Offer(1, state.Links.Length > 0, () => Unlink(Pick(state.Links)));
        Offer(3, any(state.Customers), () => AddNote(state));
        Offer(
            2,
            any(state.Notes) && any(state.Customers),
            () => EditNote(state, Pick(state.Notes))
        );
        Offer(1, any(state.Notes), () => DeleteNote(Pick(state.Notes)));
        Offer(state.Vehicles.Length < 5 ? 2 : 0, true, AddVehicle);
        Offer(1, any(state.Vehicles), () => EditVehicle(Pick(state.Vehicles)));
        Offer(1, any(state.Vehicles), () => DeleteVehicle(Pick(state.Vehicles)));
        Offer(1, any(state.Vehicles), () => DeleteVehicleThroughBaseType(Pick(state.Vehicles)));
        Offer(2, any(state.Trucks), () => AddTrailer(state));
        Offer(1, any(state.Trailers) && any(state.Trucks), () => MoveTrailer(state));
        Offer(1, any(state.Trailers), () => DeleteTrailer(Pick(state.Trailers)));
        Offer(3, any(state.Customers), () => AddShipment(state));
        Offer(
            3,
            any(state.Shipments) && any(state.Customers),
            () => EditShipment(state, Pick(state.Shipments))
        );
        Offer(1, any(state.Shipments), () => DeleteShipment(Pick(state.Shipments)));
        Offer(bulk ? 3 : 0, any(state.Customers), () => BulkEditOrders(Pick(state.Customers)));
        Offer(bulk ? 3 : 0, state.Customers.Length > 1, () => BulkMoveOrders(state));
        Offer(bulk ? 2 : 0, any(state.Customers), () => BulkDeleteCustomer(Pick(state.Customers)));

        var roll = random.Next(changes.Sum(change => change.Weight));
        foreach (var (weight, make) in changes)
        {
            if (roll < weight)
            {
                return await make();
            }

            roll -= weight;
        }

        throw new InvalidOperationException("No change was picked.");
    }

    private Task<string> AddCustomer()
    {
        db.Customers.Add(new Customer { Name = Pick(_names) });
        return Task.FromResult("add a customer");
    }

    private async Task<string> RenameCustomer(int id)
    {
        (await db.Customers.SingleAsync(c => c.Id == id)).Name = Pick(_names);
        return $"rename customer {id}";
    }

    private async Task<string> DeleteCustomer(int id)
    {
        db.Customers.Remove(await db.Customers.SingleAsync(c => c.Id == id));
        return $"delete customer {id}";
    }

    private Task<string> DeleteCustomerUnloaded(int id)
    {
        db.Customers.Remove(Tracked<Customer>(c => c.Id == id) ?? new Customer { Id = id });
        return Task.FromResult($"delete customer {id} without loading it");
    }

    private Task<string> AddOrder(State state)
    {
        var order = new Order
        {
            CustomerId = Pick(state.Customers),
            Item = Item(),
            CouponId = MaybeCoupon(state),
        };
        db.Orders.Add(order);
        return Task.FromResult($"add an order for customer {order.CustomerId}");
    }

    private async Task<string> EditOrder(State state, int id)
    {
        var order = await db.Orders.SingleAsync(o => o.Id == id);
        switch (random.Next(3))
        {
            case 0:
                order.Item = order.Item == "big" ? "small" : "big";
                return $"make order {id} {order.Item}";
            case 1:
                order.CustomerId = Pick(state.Customers);
                return $"move order {id} to customer {order.CustomerId}";
            default:
                order.CouponId = MaybeCoupon(state);
                return $"set order {id}'s coupon to {order.CouponId?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
        }
    }

    // Update() on an object the context didn't load, as a generic repository does, moving it to another customer.
    private async Task<string> UpdateDetachedOrder(State state, int id)
    {
        var current = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        if (Tracked<Order>(o => o.Id == id) is { } tracked)
        {
            db.Entry(tracked).State = EntityState.Detached;
        }

        db.Orders.Update(
            new Order
            {
                Id = id,
                CustomerId = Pick(state.Customers),
                Item = Item(),
                CouponId = current.CouponId,
            }
        );
        return $"update detached order {id}";
    }

    private async Task<string> DeleteOrder(int id)
    {
        db.Orders.Remove(await db.Orders.SingleAsync(o => o.Id == id));
        return $"delete order {id}";
    }

    // A stub with its nullable coupon key as stored, and its customer key left at the sentinel: Sluice can only tell
    // a stub's parent is unknown when its key holds the sentinel.
    private async Task<string> DeleteOrderUnloaded(int id)
    {
        var coupon = await db.Orders.Where(o => o.Id == id).Select(o => o.CouponId).SingleAsync();
        db.Orders.Remove(
            Tracked<Order>(o => o.Id == id) ?? new Order { Id = id, CouponId = coupon }
        );
        return $"delete order {id} without loading it";
    }

    private async Task<string> AddLine(State state, int orderId)
    {
        var stored = await db
            .OrderLines.Where(l => l.OrderId == orderId)
            .MaxAsync(l => (int?)l.LineNumber);
        var staged = db
            .ChangeTracker.Entries<OrderLine>()
            .Where(e => e.Entity.OrderId == orderId)
            .Max(e => (int?)e.Entity.LineNumber);
        var line = new OrderLine
        {
            OrderId = orderId,
            LineNumber = Math.Max(stored ?? 0, staged ?? 0) + 1,
            CouponId = MaybeCoupon(state),
        };
        db.OrderLines.Add(line);
        return $"add line {line.LineNumber} to order {orderId}";
    }

    private async Task<string> EditLine(State state, (int OrderId, int LineNumber) key)
    {
        var line = await db.OrderLines.SingleAsync(l =>
            l.OrderId == key.OrderId && l.LineNumber == key.LineNumber
        );
        line.CouponId = MaybeCoupon(state);
        return $"set line {key}'s coupon";
    }

    private async Task<string> DeleteLine((int OrderId, int LineNumber) key)
    {
        db.OrderLines.Remove(
            await db.OrderLines.SingleAsync(l =>
                l.OrderId == key.OrderId && l.LineNumber == key.LineNumber
            )
        );
        return $"delete line {key}";
    }

    private Task<string> AddCoupon()
    {
        db.Coupons.Add(new Coupon());
        return Task.FromResult("add a coupon");
    }

    private async Task<string> DeleteCoupon(int id)
    {
        db.Coupons.Remove(await db.Coupons.SingleAsync(c => c.Id == id));
        return $"delete coupon {id}";
    }

    private Task<string> Link(State state)
    {
        var (customer, coupon) = (Pick(state.Customers), Pick(state.Coupons));
        if (state.Links.Contains((customer, coupon)))
        {
            return Task.FromResult("link nothing");
        }

        db.CustomerCoupons.Add(
            new Dictionary<string, object> { ["CustomerId"] = customer, ["CouponId"] = coupon }
        );
        return Task.FromResult($"give customer {customer} coupon {coupon}");
    }

    private async Task<string> Unlink((int Customer, int Coupon) link)
    {
        db.CustomerCoupons.Remove(
            await db.CustomerCoupons.SingleAsync(l =>
                EF.Property<int>(l, "CustomerId") == link.Customer
                && EF.Property<int>(l, "CouponId") == link.Coupon
            )
        );
        return $"take coupon {link.Coupon} from customer {link.Customer}";
    }

    private Task<string> AddNote(State state)
    {
        var note = new Note
        {
            Text = Pick(_names),
            ReplyToId = state.Notes.Length > 0 && random.Next(2) == 0 ? Pick(state.Notes) : null,
        };
        db.Notes.Add(note);
        db.Entry(note).Property("CustomerId").CurrentValue = Pick(state.Customers);
        return Task.FromResult($"add a note replying to {note.ReplyToId}");
    }

    private async Task<string> EditNote(State state, int id)
    {
        var note = await db.Notes.SingleAsync(n => n.Id == id);
        switch (random.Next(3))
        {
            case 0:
                note.Text = Pick(_names);
                return $"edit note {id}";
            case 1:
                var customer = Pick(state.Customers);
                db.Entry(note).Property("CustomerId").CurrentValue = customer;
                return $"move note {id} to customer {customer}";
            default:
                // Every reply points at a lower id, so replies never form a cycle EF can't order a delete through.
                var replyTo = Pick(state.Notes);
                note.ReplyToId = replyTo < id ? replyTo : null;
                return $"make note {id} reply to {note.ReplyToId}";
        }
    }

    private async Task<string> DeleteNote(int id)
    {
        db.Notes.Remove(await db.Notes.SingleAsync(n => n.Id == id));
        return $"delete note {id}";
    }

    private Task<string> AddVehicle()
    {
        db.Vehicles.Add(
            random.Next(2) == 0
                ? new Truck { Plate = Pick(_plates), Axles = random.Next(2, 5) }
                : new Vehicle { Plate = Pick(_plates) }
        );
        return Task.FromResult("add a vehicle");
    }

    private async Task<string> EditVehicle(int id)
    {
        var vehicle = await db.Vehicles.SingleAsync(v => v.Id == id);
        if (vehicle is Truck truck && random.Next(2) == 0)
        {
            truck.Axles = random.Next(2, 5);
            return $"change truck {id}'s axles";
        }

        vehicle.Plate = Pick(_plates);
        return $"change vehicle {id}'s plate";
    }

    private async Task<string> DeleteVehicle(int id)
    {
        db.Vehicles.Remove(await db.Vehicles.SingleAsync(v => v.Id == id));
        return $"delete vehicle {id}";
    }

    // A plain vehicle stub, though the row may be a truck's.
    private Task<string> DeleteVehicleThroughBaseType(int id)
    {
        db.Vehicles.Remove(Tracked<Vehicle>(v => v.Id == id) ?? new Vehicle { Id = id });
        return Task.FromResult($"delete vehicle {id} through a base-type stub");
    }

    private Task<string> AddTrailer(State state)
    {
        var trailer = new Trailer { TruckId = Pick(state.Trucks) };
        db.Trailers.Add(trailer);
        return Task.FromResult($"add a trailer to truck {trailer.TruckId}");
    }

    private async Task<string> MoveTrailer(State state)
    {
        var id = Pick(state.Trailers);
        var trailer = await db.Trailers.SingleAsync(t => t.Id == id);
        trailer.TruckId = Pick(state.Trucks);
        return $"move trailer {id} to truck {trailer.TruckId}";
    }

    private async Task<string> DeleteTrailer(int id)
    {
        db.Trailers.Remove(await db.Trailers.SingleAsync(t => t.Id == id));
        return $"delete trailer {id}";
    }

    private Task<string> AddShipment(State state)
    {
        var shipment = new Shipment
        {
            CustomerId = Pick(state.Customers),
            CouponId = MaybeCoupon(state),
        };
        db.Shipments.Add(shipment);
        return Task.FromResult($"add a shipment for customer {shipment.CustomerId}");
    }

    private async Task<string> EditShipment(State state, int id)
    {
        var shipment = await db.Shipments.SingleAsync(s => s.Id == id);
        switch (random.Next(4))
        {
            case 0:
                shipment.Note = Pick(_names);
                return $"edit shipment {id}";
            case 1:
                shipment.CustomerId = Pick(state.Customers);
                return $"move shipment {id} to customer {shipment.CustomerId}";
            case 2:
                shipment.CouponId = MaybeCoupon(state);
                return $"set shipment {id}'s coupon to {shipment.CouponId?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
            default:
                // Writes only the legs' table, so EF doesn't check the shipment's row version.
                shipment.Legs.Add(new Leg { Place = Pick(_names) });
                return $"add a leg to shipment {id}";
        }
    }

    private async Task<string> DeleteShipment(int id)
    {
        db.Shipments.Remove(await db.Shipments.SingleAsync(s => s.Id == id));
        return $"delete shipment {id}";
    }

    private async Task<string> BulkEditOrders(int customer)
    {
        var item = Item();
        await db
            .Orders.Where(o => o.CustomerId == customer)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.Item, item));
        return $"bulk-make customer {customer}'s orders {item}";
    }

    // Re-parents rows in bulk: both customers' lists change.
    private async Task<string> BulkMoveOrders(State state)
    {
        var (from, to) = (Pick(state.Customers), Pick(state.Customers));
        await db
            .Orders.Where(o => o.CustomerId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.CustomerId, to));
        return $"bulk-move customer {from}'s orders to customer {to}";
    }

    private async Task<string> BulkDeleteCustomer(int id)
    {
        await db.Customers.Where(c => c.Id == id).ExecuteDeleteAsync();
        return $"bulk-delete customer {id}";
    }

    private static bool IsFrom<T>(Exception? exception)
        where T : Exception =>
        exception is not null && (exception is T || IsFrom<T>(exception.InnerException));

    private T? Tracked<T>(Func<T, bool> matches)
        where T : class =>
        db.ChangeTracker.Entries<T>().Select(e => e.Entity).FirstOrDefault(matches);

    private T Pick<T>(T[] rows) => rows[random.Next(rows.Length)];

    private string Item() => random.Next(2) == 0 ? "big" : "small";

    private int? MaybeCoupon(State state) =>
        state.Coupons.Length > 0 && random.Next(3) > 0 ? Pick(state.Coupons) : null;

    private sealed record State(
        int[] Customers,
        int[] Orders,
        (int OrderId, int LineNumber)[] Lines,
        int[] Coupons,
        (int Customer, int Coupon)[] Links,
        int[] Notes,
        int[] Vehicles,
        int[] Trucks,
        int[] Trailers,
        int[] Shipments
    )
    {
        public static async Task<State> Of(ShopDb db, Keys first)
        {
            var lines = await db
                .OrderLines.Where(l => l.OrderId > first.Order)
                .Select(l => new { l.OrderId, l.LineNumber })
                .ToListAsync();
            var links = await db
                .CustomerCoupons.Where(l => EF.Property<int>(l, "CustomerId") > first.Customer)
                .Select(l => new
                {
                    Customer = EF.Property<int>(l, "CustomerId"),
                    Coupon = EF.Property<int>(l, "CouponId"),
                })
                .ToListAsync();

            return new(
                await db
                    .Customers.Where(c => c.Id > first.Customer)
                    .Select(c => c.Id)
                    .ToArrayAsync(),
                await db.Orders.Where(o => o.Id > first.Order).Select(o => o.Id).ToArrayAsync(),
                [.. lines.Select(l => (l.OrderId, l.LineNumber))],
                await db.Coupons.Where(c => c.Id > first.Coupon).Select(c => c.Id).ToArrayAsync(),
                [.. links.Select(l => (l.Customer, l.Coupon))],
                await db.Notes.Where(n => n.Id > first.Note).Select(n => n.Id).ToArrayAsync(),
                await db.Vehicles.Where(v => v.Id > first.Vehicle).Select(v => v.Id).ToArrayAsync(),
                await db
                    .Vehicles.Where(v => v.Id > first.Vehicle)
                    .OfType<Truck>()
                    .Select(t => t.Id)
                    .ToArrayAsync(),
                await db.Trailers.Where(t => t.Id > first.Trailer).Select(t => t.Id).ToArrayAsync(),
                await db
                    .Shipments.Where(s => s.Id > first.Shipment)
                    .Select(s => s.Id)
                    .ToArrayAsync()
            );
        }
    }
}
