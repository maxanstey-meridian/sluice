using Microsoft.EntityFrameworkCore;

namespace Sluice.EntityFrameworkCore.Tests.Support;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
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

/// <summary>An order line has a nullable FK to a coupon.</summary>
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
    public DbSet<CustomerTotal> CustomerTotals => Set<CustomerTotal>();
    public DbSet<ActiveCustomer> ActiveCustomers => Set<ActiveCustomer>();
    public DbSet<TopCustomer> TopCustomers => Set<TopCustomer>();
    public DbSet<RecentOrder> RecentOrders => Set<RecentOrder>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("citext");

        model.Entity<Note>().HasOne<Customer>().WithMany().HasForeignKey("CustomerId");
        model.Entity<OrderLine>().HasKey(line => new { line.OrderId, line.LineNumber });
        model.Entity<OrderLine>().HasOne<Order>().WithMany().HasForeignKey(line => line.OrderId);
        model.Entity<OrderLine>().HasOne<Coupon>().WithMany().HasForeignKey(line => line.CouponId);
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

        model.Entity<CustomerTotal>().HasNoKey().ToView("customer_totals");
        model.Entity<ActiveCustomer>().ToView("active_customers");
        model.Entity<TopCustomer>().ToSqlQuery("select 1 as \"Id\"");
        model.Entity<RecentOrder>().ToFunction("recent_orders");
    }
}
