using Microsoft.EntityFrameworkCore;

namespace Sluice.EntityFrameworkCore.Tests.Support;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Many-to-many, through the shared-type join entity <c>CustomerCoupons</c>.</summary>
    public List<Coupon> Coupons { get; set; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Item { get; set; } = "";
}

/// <summary>Points at its customer through a shadow FK, <c>CustomerId</c>.</summary>
public sealed class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>An order line has a composite key and a nullable FK to a coupon, set to null when the coupon is deleted.</summary>
public sealed class OrderLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public int? CouponId { get; set; }
}

public sealed class Coupon
{
    public int Id { get; set; }
}

public readonly record struct UserId(int Value);

public sealed class User
{
    public UserId Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Price
{
    public decimal Amount { get; set; }
    public string Label { get; set; } = "";
}

public sealed class Tag
{
    public string Name { get; set; } = "";
}

/// <summary>A typed id that, unlike a record struct, defines no <c>==</c>.</summary>
public readonly struct InvoiceNo(int value)
{
    public int Value { get; } = value;
}

public sealed class Invoice
{
    public InvoiceNo No { get; set; }
    public string Payee { get; set; } = "";
}

public readonly record struct Sku(string Value);

/// <summary>Keyed by a strongly-typed <see cref="Sku"/>, stored as case-insensitive text.</summary>
public sealed class Product
{
    public Sku Sku { get; set; }
    public string Name { get; set; } = "";
}

public abstract class Animal
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Dog : Animal
{
    public bool Fetches { get; set; }
}

/// <summary>Mapped table-per-type: a truck's own columns live in a table of their own.</summary>
public abstract class Vehicle
{
    public int Id { get; set; }
    public string Plate { get; set; } = "";
}

public sealed class Truck : Vehicle
{
    public int Axles { get; set; }
}

/// <summary>
/// Owns an address (which owns a contact in turn), stored in the parcel's own row but tracked as entries of their own,
/// and stops, stored in a table of their own.
/// </summary>
public sealed class Parcel
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Address Address { get; set; } = new();
    public List<Stop> Stops { get; set; } = [];
}

public sealed class Address
{
    public string Street { get; set; } = "";
    public Contact Contact { get; set; } = new();
}

public sealed class Contact
{
    public string Phone { get; set; } = "";
}

public sealed class Stop
{
    public string Place { get; set; } = "";
}

/// <summary>Known to its bins by its code, an alternate key.</summary>
public sealed class Warehouse
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
}

public sealed class Bin
{
    public int Id { get; set; }
    public string WarehouseCode { get; set; } = "";
}

/// <summary>Written to a table, read through a view, so the read helpers refuse it.</summary>
public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CustomerTotal
{
    public int CustomerId { get; set; }
    public int Orders { get; set; }
}

public sealed class ActiveCustomer
{
    public int Id { get; set; }
}

public sealed class TopCustomer
{
    public int Id { get; set; }
}

public sealed class RecentOrder
{
    public int Id { get; set; }
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Price> Prices => Set<Price>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Animal> Animals => Set<Animal>();
    public DbSet<Dog> Dogs => Set<Dog>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Parcel> Parcels => Set<Parcel>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Bin> Bins => Set<Bin>();
    public DbSet<Dictionary<string, object>> CustomerCoupons =>
        Set<Dictionary<string, object>>("CustomerCoupons");
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<CustomerTotal> CustomerTotals => Set<CustomerTotal>();
    public DbSet<ActiveCustomer> ActiveCustomers => Set<ActiveCustomer>();
    public DbSet<TopCustomer> TopCustomers => Set<TopCustomer>();
    public DbSet<RecentOrder> RecentOrders => Set<RecentOrder>();

    /// <summary>Adds two customers, Ada and Bob, then forgets them, so later queries load them afresh.</summary>
    public async Task<(int Ada, int Bob)> AddCustomers()
    {
        var ada = new Customer { Name = "Ada" };
        var bob = new Customer { Name = "Bob" };
        Customers.AddRange(ada, bob);
        await SaveChangesAsync();
        ChangeTracker.Clear();
        return (ada.Id, bob.Id);
    }

    /// <summary>Loads customer <paramref name="id"/>, renames it and saves.</summary>
    public async Task Rename(int id, string name)
    {
        (await Customers.SingleAsync(c => c.Id == id)).Name = name;
        await SaveChangesAsync();
    }

    /// <summary>Customer <paramref name="id"/>'s name as the database holds it now.</summary>
    public Task<string> NameOf(int id) =>
        Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).SingleAsync();

    /// <summary>The key the next inserted customer gets, as long as nothing else inserts one first.</summary>
    public Task<int> NextCustomerId() =>
        Database
            .SqlQueryRaw<int>(
                """
                select (coalesce(pg_sequence_last_value(pg_get_serial_sequence('"Customers"', 'Id')), 0) + 1)::int as "Value"
                """
            )
            .SingleAsync();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("citext");

        model.Entity<Note>().HasOne<Customer>().WithMany().HasForeignKey("CustomerId");
        model.Entity<OrderLine>().HasKey(line => new { line.OrderId, line.LineNumber });
        model.Entity<OrderLine>().HasOne<Order>().WithMany().HasForeignKey(line => line.OrderId);
        model
            .Entity<OrderLine>()
            .HasOne<Coupon>()
            .WithMany()
            .HasForeignKey(line => line.CouponId)
            .OnDelete(DeleteBehavior.SetNull);
        model
            .Entity<User>()
            .Property(user => user.Id)
            .HasConversion(id => id.Value, value => new UserId(value))
            .ValueGeneratedNever();
        model.Entity<Invoice>().HasKey(invoice => invoice.No);
        model
            .Entity<Invoice>()
            .Property(invoice => invoice.No)
            .HasConversion(no => no.Value, value => new InvoiceNo(value))
            .ValueGeneratedNever();
        model.Entity<Price>().HasKey(price => price.Amount);
        model.Entity<Price>().Property(price => price.Amount).HasColumnType("numeric(10,2)");
        model.Entity<Tag>().HasKey(tag => tag.Name);
        model.Entity<Tag>().Property(tag => tag.Name).HasColumnType("citext");
        model.Entity<Product>().HasKey(product => product.Sku);
        model
            .Entity<Product>()
            .Property(product => product.Sku)
            .HasConversion(sku => sku.Value, value => new Sku(value))
            .HasColumnType("citext");

        model.Entity<Vehicle>().UseTptMappingStrategy();
        model.Entity<Truck>();
        model.Entity<Parcel>().HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId);
        model.Entity<Parcel>().OwnsOne(p => p.Address, address => address.OwnsOne(a => a.Contact));
        model.Entity<Parcel>().OwnsMany(p => p.Stops);
        model
            .Entity<Bin>()
            .HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(bin => bin.WarehouseCode)
            .HasPrincipalKey(warehouse => warehouse.Code);
        model
            .Entity<Customer>()
            .HasMany(c => c.Coupons)
            .WithMany()
            .UsingEntity(
                "CustomerCoupons",
                coupon => coupon.HasOne(typeof(Coupon)).WithMany().HasForeignKey("CouponId"),
                customer => customer.HasOne(typeof(Customer)).WithMany().HasForeignKey("CustomerId")
            );

        model.Entity<Supplier>().ToTable("Suppliers").ToView("supplier_names");
        model.Entity<CustomerTotal>().HasNoKey().ToView("customer_totals");
        model.Entity<ActiveCustomer>().ToView("active_customers");
        model.Entity<TopCustomer>().ToSqlQuery("select 1 as \"Id\"");
        model.Entity<RecentOrder>().ToFunction("recent_orders");
    }
}
