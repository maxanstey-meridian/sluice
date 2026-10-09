using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Sluice.EntityFrameworkCore.Tests.Support;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Many-to-many, through the shared-type join entity <c>CustomerCoupons</c>.</summary>
    public List<Coupon> Coupons { get; set; } = [];
}

/// <summary>What generic repository code would constrain a type parameter to.</summary>
public interface IHasCustomer
{
    public Customer? Customer { get; }
}

/// <summary>Deleted with its customer, and has a nullable FK to a coupon, set to null when the coupon is deleted.</summary>
public sealed class Order : IHasCustomer
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Item { get; set; } = "";
    public int? CouponId { get; set; }
}

/// <summary>
/// Points at its customer through a shadow FK, <c>CustomerId</c>, and may reply to another note, which deletes it
/// with that note.
/// </summary>
public sealed class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public int? ReplyToId { get; set; }
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

/// <summary>
/// Has PostgreSQL's <c>xmin</c> as its row version, a concurrency token the database changes on every write, so EF
/// fails a save of one unless the row is unchanged since it was loaded. Its coupon key is nullable, set to null when the
/// coupon is deleted. Owns legs, stored in a table of their own.
/// </summary>
public sealed class Shipment
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int? CouponId { get; set; }
    public string Note { get; set; } = "";
    public uint Version { get; set; }
    public List<Leg> Legs { get; set; } = [];
}

public sealed class Leg
{
    public string Place { get; set; } = "";
}

/// <summary>Has no row version, but its customer key is itself a concurrency token, which only the app changes.</summary>
public sealed class Booking
{
    public int Id { get; set; }

    [ConcurrencyCheck]
    public int CustomerId { get; set; }

    public string Note { get; set; } = "";
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

/// <summary>Has a navigation its base type doesn't, which plain queries load automatically.</summary>
public sealed class Dog : Animal
{
    public bool Fetches { get; set; }
    public int? OwnerId { get; set; }
    public Customer? Owner { get; set; }
}

/// <summary>
/// Mapped table-per-type: a truck's own columns live in a table of their own. Not abstract, so a truck's row can be
/// deleted through a plain vehicle.
/// </summary>
public class Vehicle
{
    public int Id { get; set; }
    public string Plate { get; set; } = "";
}

public sealed class Truck : Vehicle
{
    public int Axles { get; set; }

    /// <summary>
    /// A foreign key only trucks have, so a vehicle's row doesn't hold it. A concurrency token, which doesn't keep
    /// eviction exact for a row split across tables.
    /// </summary>
    public int? OwnerId { get; set; }
}

/// <summary>Points at trucks only, and the database deletes it with its truck.</summary>
public sealed class Trailer
{
    public int Id { get; set; }
    public int TruckId { get; set; }
}

/// <summary>Mapped table-per-concrete-type: the abstract shape has no table; each concrete shape has its own.</summary>
public abstract class Shape
{
    public int Id { get; set; }
}

public sealed class Circle : Shape
{
    public int Radius { get; set; }
}

/// <summary>
/// Owns an address (which owns a contact in turn), stored in the parcel's own row but tracked as entries of their own,
/// stops, stored in a table of their own, and a label, stored as a JSON column.
/// </summary>
public sealed class Parcel
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Address Address { get; set; } = new();
    public List<Stop> Stops { get; set; } = [];
    public Label Label { get; set; } = new();
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

public sealed class Label
{
    public string Text { get; set; } = "";
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

/// <summary>Mapped to no table, view, function or SQL query, so the read helpers refuse it.</summary>
public sealed class Unstored
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

/// <summary>Mapped table-per-concrete-type, with one concrete type read through a view and one stored in a table.</summary>
public abstract class Promotion
{
    public int Id { get; set; }
}

public sealed class LivePromotion : Promotion;

public sealed class StoredPromotion : Promotion
{
    public string Code { get; set; } = "";
}

/// <summary>
/// Stored in a table and mapped table-per-concrete-type, with a derived type read through a view, which a query of
/// banners reads too.
/// </summary>
public class Banner
{
    public int Id { get; set; }
}

public sealed class LiveBanner : Banner;

/// <summary>Loads its customer lazily, through the <see cref="ILazyLoader"/> EF passes its constructor.</summary>
public sealed class Review
{
    private readonly ILazyLoader? _loader;
    private Customer? _customer;

    public Review() { }

    public Review(ILazyLoader loader) => _loader = loader;

    public int Id { get; set; }
    public int CustomerId { get; set; }

    public Customer? Customer
    {
        get
        {
            _loader?.Load(this);
            return _customer;
        }
        set => _customer = value;
    }
}

/// <summary>Loads its customer lazily, through the lazy loader delegate EF passes its constructor.</summary>
public sealed class Rating
{
    private readonly Action<object, string>? _lazyLoader;
    private Customer? _customer;

    public Rating() { }

    public Rating(Action<object, string> lazyLoader) => _lazyLoader = lazyLoader;

    public int Id { get; set; }
    public int CustomerId { get; set; }

    public Customer? Customer
    {
        get
        {
            _lazyLoader?.Invoke(this, nameof(Customer));
            return _customer;
        }
        set => _customer = value;
    }
}

/// <summary>Owns a signature that loads its signer lazily, though the delivery itself loads nothing lazily.</summary>
public sealed class Delivery
{
    public int Id { get; set; }
    public Signature Signature { get; set; } = new();
}

public sealed class Signature
{
    private readonly ILazyLoader? _loader;
    private Customer? _signer;

    public Signature() { }

    public Signature(ILazyLoader loader) => _loader = loader;

    public int? SignerId { get; set; }

    public Customer? Signer
    {
        get
        {
            _loader?.Load(this);
            return _signer;
        }
        set => _signer = value;
    }
}

/// <summary>Auto-includes its songs, many-to-many through the shared-type join entity <c>PlaylistSongs</c>.</summary>
public sealed class Playlist
{
    public int Id { get; set; }
    public List<Song> Songs { get; set; } = [];
}

public sealed class Song
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>Owns what it's playing, which auto-includes its song.</summary>
public sealed class Jukebox
{
    public int Id { get; set; }
    public NowPlaying NowPlaying { get; set; } = new();
}

public sealed class NowPlaying
{
    public int? SongId { get; set; }
    public Song? Song { get; set; }
}

/// <summary>Auto-includes the review it was given for, which loads lazily.</summary>
public sealed class Award
{
    public int Id { get; set; }
    public int ReviewId { get; set; }
    public Review? Review { get; set; }
}

/// <summary>Holds the context that loaded it, which EF passes its constructor.</summary>
public sealed class Basket
{
    public Basket() { }

    public Basket(DbContext context) => Context = context;

    public int Id { get; set; }

    [NotMapped]
    public DbContext? Context { get; }
}

/// <summary>Gets a lazy loader through a service property, as lazy-loading proxies do.</summary>
public sealed class Receipt
{
    public int Id { get; set; }
    public ILazyLoader? Loader { get; set; }
}

/// <summary>
/// Has two named query filters: <c>open</c> on its own column, and <c>customer</c> through its customer navigation.
/// </summary>
public sealed class Ticket
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public bool Closed { get; set; }
}

/// <summary>Has a query filter with a subquery over customers.</summary>
public sealed class Memo
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
}

/// <summary>Has a query filter on the context's tenant.</summary>
public sealed class Account
{
    public int Id { get; set; }
    public int TenantId { get; set; }
}

/// <summary>
/// Has two named query filters, as a multi-tenant app with soft delete would: <c>Tenant</c> on the context's tenant,
/// and <c>SoftDelete</c> on its own column.
/// </summary>
public sealed class Issue
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Auto-includes its issue, and points at an account, each of which has a query filter on the tenant.</summary>
public sealed class Watch
{
    public int Id { get; set; }
    public int? IssueId { get; set; }
    public Issue? Issue { get; set; }
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
}

/// <summary>The current tenant, held in shared state as an app's ambient accessor would hold it.</summary>
public static class TenantAccessor
{
    /// <summary>The tenant when none is set, which anything can change.</summary>
    public static int Fallback;

    /// <summary>The tenant of the current flow, in a readonly field holding an object.</summary>
    public static readonly AsyncLocal<int> Flow = new();

    public static int Current { get; set; }

    public static int Read() => Current;

    /// <summary>The current tenant, through a lambda of the caller's.</summary>
    public static int Map(Func<int, int> pick) => pick(Current);
}

/// <summary>An object a query filter captures, which reads the current tenant when the query runs.</summary>
public sealed class TenantSource
{
    public int Current => TenantAccessor.Current;
}

/// <summary>
/// Configures query filters of <see cref="Ledger"/>'s that read this object's own state: <c>instance method</c>
/// through a method, and <c>field</c> a field anything can set.
/// </summary>
public sealed class LedgerFilters : IEntityTypeConfiguration<Ledger>
{
    public int TenantId;

    public void Configure(EntityTypeBuilder<Ledger> builder)
    {
        builder.HasQueryFilter("instance method", ledger => ledger.TenantId == Tenant());
        builder.HasQueryFilter("field", ledger => ledger.TenantId == TenantId);
    }

    private int Tenant() => TenantAccessor.Current;
}

public enum LedgerKind
{
    Sales,
    Void,
}

/// <summary>
/// Has named query filters that read shared state, one way each: <c>accessor</c> a static property, <c>method</c> a
/// static method, <c>clock</c> the time, <c>captured</c> an object it captured, <c>static field</c> a static field
/// that isn't readonly, <c>async local</c> a readonly static <c>AsyncLocal</c>, <c>new object</c> an object it makes,
/// <c>lambda</c> a static method given a lambda, <c>struct</c> a captured struct holding an object, <c>database
/// function</c> a built-in database function given no column, and <see cref="LedgerFilters"/>' two; and <c>plain</c>,
/// which reads only what can't change between calls.
/// </summary>
public sealed class Ledger
{
    public static readonly string Closed = "closed";
    public static readonly int NoTenant = -1;
    public static readonly Guid NoBatch = Guid.Empty;

    public static string Region { get; } = "eu";

    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Code { get; set; } = "";
    public DateTime PostedAt { get; set; }
    public bool IsDeleted { get; set; }
    public LedgerKind Kind { get; set; }
    public Guid Batch { get; set; }
}

/// <summary>
/// Auto-includes its ticket, and has a query filter whose subquery calls <c>IgnoreAutoIncludes()</c> and
/// <c>IgnoreQueryFilters()</c>, which the analysis mustn't take for the whole query's.
/// </summary>
public sealed class Voucher
{
    public int Id { get; set; }
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
}

/// <summary>Auto-includes its dog, which auto-includes its owner in turn.</summary>
public sealed class Kennel
{
    public int Id { get; set; }
    public int? DogId { get; set; }
    public Dog? Dog { get; set; }
}

public abstract class Pet
{
    public int Id { get; set; }
}

/// <summary>Owns its perch, which auto-includes the song it's learning, though its base type owns nothing.</summary>
public sealed class Parrot : Pet
{
    public Perch Perch { get; set; } = new();
}

public sealed class Perch
{
    public int? SongId { get; set; }
    public Song? Song { get; set; }
}

/// <summary>A tenant scope the context holds as a whole, so a configure sets it with a new one.</summary>
public readonly record struct TenantScope(int Tenant);

/// <summary>Context state through an interface, whose implementing getter is virtual but sealed.</summary>
public interface ITenanted
{
    public int TenantId { get; }
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options), ITenanted
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Price> Prices => Set<Price>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Animal> Animals => Set<Animal>();
    public DbSet<Dog> Dogs => Set<Dog>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Trailer> Trailers => Set<Trailer>();
    public DbSet<Parcel> Parcels => Set<Parcel>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Bin> Bins => Set<Bin>();
    public DbSet<Dictionary<string, object>> CustomerCoupons =>
        Set<Dictionary<string, object>>("CustomerCoupons");
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<CustomerTotal> CustomerTotals => Set<CustomerTotal>();
    public DbSet<Unstored> Unstored => Set<Unstored>();
    public DbSet<TopCustomer> TopCustomers => Set<TopCustomer>();
    public DbSet<RecentOrder> RecentOrders => Set<RecentOrder>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Memo> Memos => Set<Memo>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<Watch> Watches => Set<Watch>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<Basket> Baskets => Set<Basket>();
    public DbSet<Shape> Shapes => Set<Shape>();
    public DbSet<Circle> Circles => Set<Circle>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<Song> Songs => Set<Song>();
    public DbSet<Dictionary<string, object>> PlaylistSongs =>
        Set<Dictionary<string, object>>("PlaylistSongs");
    public DbSet<Jukebox> Jukeboxes => Set<Jukebox>();
    public DbSet<Award> Awards => Set<Award>();
    public DbSet<Ledger> Ledgers => Set<Ledger>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<Kennel> Kennels => Set<Kennel>();
    public DbSet<Pet> Pets => Set<Pet>();

    /// <summary>The tenant the query filters of <see cref="Account"/> and <see cref="Issue"/> read.</summary>
    public int TenantId { get; set; }

    // Context state read.Db's configure can't set: it keeps what the constructor gave it.
    private readonly int _fixedTenant = -1;

    /// <summary>Context state set only by an object initializer, which read.Db's configure can't set either.</summary>
    public int SeededTenant { get; init; } = -1;

    /// <summary>Context state with no setter at all.</summary>
    public int DefaultTenant { get; } = -1;

    /// <summary>Settable as a whole, so the read-only parts of the struct it holds are the configure's too.</summary>
    public TenantScope Scope { get; set; } = new(-1);

    /// <summary>A database function of the app's, which could read any table.</summary>
    [DbFunction("customer_name")]
    public static string CustomerName(int id) => throw new NotSupportedException();

    /// <summary>The database's own <c>upper</c>, mapped as a built-in function.</summary>
    [DbFunction("upper", IsBuiltIn = true)]
    public static string Upper(string text) => throw new NotSupportedException();

    /// <summary>The database's own <c>current_setting</c>, mapped as a built-in function.</summary>
    [DbFunction("current_setting", IsBuiltIn = true)]
    public static string? Setting(string name, bool missingOk) => throw new NotSupportedException();

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

    /// <summary>A user key no row has yet, nor any larger one: one more than the largest.</summary>
    public Task<int> UnusedUserId() =>
        Database
            .SqlQueryRaw<int>(
                """
                select (coalesce(max("Id"), 0) + 1)::int as "Value" from "Users"
                """
            )
            .SingleAsync();

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
        model
            .Entity<Note>()
            .HasOne<Note>()
            .WithMany()
            .HasForeignKey(note => note.ReplyToId)
            .OnDelete(DeleteBehavior.Cascade);
        model
            .Entity<Order>()
            .HasOne<Coupon>()
            .WithMany()
            .HasForeignKey(order => order.CouponId)
            .OnDelete(DeleteBehavior.SetNull);
        model.Entity<Shipment>().HasOne<Customer>().WithMany().HasForeignKey(s => s.CustomerId);
        model
            .Entity<Shipment>()
            .HasOne<Coupon>()
            .WithMany()
            .HasForeignKey(s => s.CouponId)
            .OnDelete(DeleteBehavior.SetNull);
        model.Entity<Shipment>().Property(s => s.Version).IsRowVersion();
        model.Entity<Shipment>().OwnsMany(s => s.Legs);
        model.Entity<Booking>().HasOne<Customer>().WithMany().HasForeignKey(b => b.CustomerId);
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
        model.Entity<Truck>().HasOne<Customer>().WithMany().HasForeignKey(t => t.OwnerId);
        model.Entity<Truck>().Property(t => t.OwnerId).IsConcurrencyToken();
        model.Entity<Trailer>().HasOne<Truck>().WithMany().HasForeignKey(t => t.TruckId);
        model.Entity<Shape>().UseTpcMappingStrategy();
        model.Entity<Parcel>().HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId);
        model.Entity<Parcel>().OwnsOne(p => p.Address, address => address.OwnsOne(a => a.Contact));
        model.Entity<Parcel>().OwnsMany(p => p.Stops);
        model.Entity<Parcel>().OwnsOne(p => p.Label, label => label.ToJson());
        model.Entity<Delivery>().OwnsOne(d => d.Signature);
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
                coupon =>
                    coupon.HasOne(typeof(Coupon), "Coupon").WithMany().HasForeignKey("CouponId"),
                customer => customer.HasOne(typeof(Customer)).WithMany().HasForeignKey("CustomerId")
            );

        model.Entity<Dog>().Navigation(dog => dog.Owner).AutoInclude();
        model.Entity<Playlist>().HasMany(p => p.Songs).WithMany().UsingEntity("PlaylistSongs");
        model.Entity<Playlist>().Navigation(p => p.Songs).AutoInclude();
        model
            .Entity<Jukebox>()
            .OwnsOne(
                j => j.NowPlaying,
                playing =>
                {
                    playing.HasOne(n => n.Song).WithMany().HasForeignKey(n => n.SongId);
                    playing.Navigation(n => n.Song).AutoInclude();
                }
            );
        model.Entity<Award>().Navigation(a => a.Review).AutoInclude();
        model.Entity<Ticket>().HasQueryFilter("open", ticket => !ticket.Closed);
        model.Entity<Ticket>().HasQueryFilter("customer", ticket => ticket.Customer!.Name != "");
        model.Entity<Memo>().HasQueryFilter(memo => Customers.Any(c => c.Id == memo.CustomerId));
        model.Entity<Account>().HasQueryFilter(account => account.TenantId == TenantId);
        model.Entity<Issue>().HasQueryFilter("Tenant", issue => issue.TenantId == TenantId);
        model.Entity<Issue>().HasQueryFilter("SoftDelete", issue => !issue.IsDeleted);
        model.Entity<Watch>().Navigation(watch => watch.Issue).AutoInclude();
        var tenants = new TenantSource();
        var scope = (Tenant: 0, Source: tenants);
        var code = "open";
        var tenant = 0;
        model
            .Entity<Ledger>()
            .HasQueryFilter("accessor", ledger => ledger.TenantId == TenantAccessor.Current);
        model
            .Entity<Ledger>()
            .HasQueryFilter("method", ledger => ledger.TenantId == TenantAccessor.Read());
        model
            .Entity<Ledger>()
            .HasQueryFilter("clock", ledger => ledger.PostedAt <= DateTime.UtcNow);
        model
            .Entity<Ledger>()
            .HasQueryFilter("captured", ledger => ledger.TenantId == tenants.Current);
        model
            .Entity<Ledger>()
            .HasQueryFilter("static field", ledger => ledger.TenantId == TenantAccessor.Fallback);
        model
            .Entity<Ledger>()
            .HasQueryFilter("async local", ledger => ledger.TenantId == TenantAccessor.Flow.Value);
        model
            .Entity<Ledger>()
            .HasQueryFilter("new object", ledger => ledger.TenantId == new TenantSource().Current);
        model
            .Entity<Ledger>()
            .HasQueryFilter("lambda", ledger => ledger.TenantId == TenantAccessor.Map(id => id));
        model.Entity<Ledger>().HasQueryFilter("struct", ledger => ledger.TenantId == scope.Tenant);
        model
            .Entity<Ledger>()
            .HasQueryFilter("readonly field", ledger => ledger.TenantId != _fixedTenant);
        model
            .Entity<Ledger>()
            .HasQueryFilter("init property", ledger => ledger.TenantId != SeededTenant);
        model
            .Entity<Ledger>()
            .HasQueryFilter("get-only property", ledger => ledger.TenantId != DefaultTenant);
        model
            .Entity<Ledger>()
            .HasQueryFilter("context struct", ledger => ledger.TenantId != Scope.Tenant);
        model
            .Entity<Ledger>()
            .HasQueryFilter(
                "database function",
                ledger => ledger.Code != Setting("app.ledger_code", true)
            );
        model.ApplyConfiguration(new LedgerFilters());
        model
            .Entity<Ledger>()
            .HasQueryFilter(
                "plain",
                ledger =>
                    !ledger.IsDeleted
                    && ledger.Kind != LedgerKind.Void
                    && ledger.Code != Ledger.Closed
                    && ledger.Code != Ledger.Region
                    && ledger.Code != code
                    && ledger.TenantId != tenant
                    && ledger.TenantId != Ledger.NoTenant
                    && ledger.Batch != Ledger.NoBatch
                    && ledger.PostedAt > DateTime.MinValue
                    && EF.Property<string>(ledger, nameof(Ledger.Code)) != "void"
                    && !ledger.Code.StartsWith("x")
                    && !EF.Functions.ILike(ledger.Code, "void%")
            );
        model.Entity<Voucher>().Navigation(voucher => voucher.Ticket).AutoInclude();
        model
            .Entity<Voucher>()
            .HasQueryFilter(voucher =>
                Coupons.IgnoreAutoIncludes().IgnoreQueryFilters().Any(c => c.Id != voucher.Id)
            );
        model.Entity<Kennel>().Navigation(kennel => kennel.Dog).AutoInclude();
        model
            .Entity<Parrot>()
            .OwnsOne(
                parrot => parrot.Perch,
                perch =>
                {
                    perch.HasOne(p => p.Song).WithMany().HasForeignKey(p => p.SongId);
                    perch.Navigation(p => p.Song).AutoInclude();
                }
            );

        model.Entity<Supplier>().ToTable("Suppliers").ToView("supplier_names");
        model.Entity<CustomerTotal>().HasNoKey().ToView("customer_totals");
        model.Entity<Unstored>().ToTable((string?)null);
        model.Entity<TopCustomer>().ToSqlQuery("select 1 as \"Id\"");
        model.Entity<RecentOrder>().ToFunction("recent_orders");
        model.Entity<Promotion>().UseTpcMappingStrategy();
        model.Entity<LivePromotion>().ToView("live_promotions");
        model.Entity<StoredPromotion>();
        model.Entity<Banner>().UseTpcMappingStrategy();
        model.Entity<LiveBanner>().ToView("live_banners");
    }
}
