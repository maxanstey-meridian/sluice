using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FoldedKeyTests(PostgresFixture postgres)
{
    private static readonly Query<decimal, Price> _price = new("ef-folded-price");
    private static readonly Query<string, Tag> _tag = new("ef-folded-tag");
    private static readonly Query<string, Product> _product = new("ef-folded-product");
    private static readonly Query<int, int> _clash = new("ef-folded-clash");

    [Fact]
    public async Task A_numeric_key_read_as_1_is_evicted_by_the_same_value_written_as_1_00()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Prices.Add(new Price { Amount = 1.00m, Label = "one" });
        await db.SaveChangesAsync();

        Task<Price> ReadOne(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Prices, 1m).SingleAsync(ct);

        (await node.Cache.GetOrSetAsync(_price, 1m, ReadOne)).Label.Should().Be("one");
        (await CachedRead.IsEvictedBy(ReadOne, db.Prices.Dependency(1.00m))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadOne, db.Prices.Dependency(1.01m))).Should().BeFalse();
    }

    [Fact]
    public async Task A_citext_key_read_in_one_case_is_evicted_by_the_same_key_in_another()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Tags.Add(new Tag { Name = "Widget" });
        await db.SaveChangesAsync();

        Task<Tag> ReadWidget(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Tags, "WIDGET").SingleAsync(ct);

        (await node.Cache.GetOrSetAsync(_tag, "WIDGET", ReadWidget)).Name.Should().Be("Widget");
        (await CachedRead.IsEvictedBy(ReadWidget, db.Tags.Dependency("widget"))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadWidget, db.Tags.Dependency("gadget"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_strongly_typed_key_folds_the_value_its_converter_stores()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Products.Add(new Product { Sku = new Sku("Tea-1"), Name = "Tea" });
        await db.SaveChangesAsync();

        Task<Product> ReadTea(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Products, new Sku("TEA-1")).SingleAsync(ct);

        (await node.Cache.GetOrSetAsync(_product, "TEA-1", ReadTea)).Name.Should().Be("Tea");
        (await CachedRead.IsEvictedBy(ReadTea, db.Products.Dependency(new Sku("tea-1"))))
            .Should()
            .BeTrue();
        (await CachedRead.IsEvictedBy(ReadTea, db.Products.Dependency(new Sku("tea-2"))))
            .Should()
            .BeFalse();
    }

    // A save reports the value it would write back for what EF loaded, so a converter that loads a stored value and
    // writes back another misses the readers of the stored value. Each model is refused the first time Sluice reads it,
    // whichever way: these tables don't exist, so a save that ran would fail on the database instead.
    public static TheoryData<string, Func<DbContextOptionsBuilder, DbContext>, string> Untrusted =>
        new()
        {
            {
                "a key's HasConversion lambdas",
                options => new UntrustedKeyDb(options.Options),
                "*Gadget.Id (ValueConverter<GadgetNo, Int32>)*[SluiceRoundTrips]*"
            },
            {
                "a foreign key's HasConversion lambdas",
                options => new UntrustedForeignKeyDb(options.Options),
                "*Gadget.ParentId (ValueConverter<Nullable<Int32>, Nullable<Int32>>)*"
            },
            {
                "a unique column's enum stored as text",
                options => new UntrustedUniqueDb(options.Options),
                "*Gadget.Colour (EnumToStringConverter<Colour>)*"
            },
            {
                "a unique date stored to the millisecond",
                options => new MillisecondDateDb(options.Options),
                "*Gadget.Made (timestamp(3) with time zone)*microsecond*"
            },
            {
                "a unique DateTime stored as a date",
                options => new DateColumnDb(options.Options),
                "*Gadget.Made (date)*microsecond*"
            },
        };

    [Theory]
    [MemberData(nameof(Untrusted))]
    public async Task A_model_with_an_unmarked_converter_on_a_key_foreign_key_or_unique_column_is_refused_at_first_use(
        string converter,
        Func<DbContextOptionsBuilder, DbContext> create,
        string message
    )
    {
        await using var node = new SluiceNode();
        await using var db = create(
            new DbContextOptionsBuilder()
                .UseNpgsql(postgres.ConnectionString)
                .UseSluice(node.Services)
        );

        var dependency = () => db.Set<Gadget>().AllDependencies();
        dependency.Should().Throw<InvalidOperationException>(converter).WithMessage(message);
        db.Add(new Gadget { Id = new GadgetNo(1) });
        var save = () => db.SaveChangesAsync();
        (await save.Should().ThrowAsync<DbUpdateException>(converter))
            .WithInnerException<InvalidOperationException>()
            .WithMessage(message);
        var bulk = () => db.Set<Gadget>().ExecuteDeleteAsync();
        await bulk.Should().ThrowAsync<InvalidOperationException>(converter).WithMessage(message);
    }

    // An app source named like a table would share its readers' tags with a key text of its own. Every source of the
    // model is built before its first write runs a command, so the clash throws there, whichever write comes first, and
    // where a read or a dependency first builds one: the table doesn't exist, so a write or read that ran would fail on
    // the database instead.
    [Fact]
    public async Task A_table_named_like_an_app_source_is_refused_before_a_save_bulk_write_or_read_runs()
    {
        _ = new Source<int>(ClashingDb.Table);
        await using var node = new SluiceNode(services: services =>
            services.AddDbContext<ClashingDb>(
                (provider, options) =>
                    options.UseNpgsql(postgres.ConnectionString).UseSluice(provider)
            )
        );
        await using var db = new ClashingDb(
            new DbContextOptionsBuilder<ClashingDb>()
                .UseNpgsql(postgres.ConnectionString)
                .UseSluice(node.Services)
                .Options
        );

        var dependency = () => db.Set<Gadget>().AllDependencies();
        dependency.Should().Throw<ArgumentException>().WithMessage($"*'{ClashingDb.Table}'*");
        var read = () =>
            node
                .Cache.GetOrSetAsync(
                    _clash,
                    1,
                    async (r, ct) =>
                    {
                        await r.Entity(r.Db<ClashingDb>().Set<Gadget>(), new GadgetNo(1))
                            .SingleOrDefaultAsync(ct);
                        return 1;
                    }
                )
                .AsTask();
        await read.Should().ThrowAsync<ArgumentException>().WithMessage($"*'{ClashingDb.Table}'*");
        db.Add(new Gadget { Id = new GadgetNo(1) });
        var save = () => db.SaveChangesAsync();
        (await save.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<ArgumentException>()
            .WithMessage($"*'{ClashingDb.Table}'*");
        var bulk = () => db.Set<Gadget>().ExecuteDeleteAsync();
        await bulk.Should().ThrowAsync<ArgumentException>().WithMessage($"*'{ClashingDb.Table}'*");
    }

    private sealed class ClashingDb(DbContextOptions<ClashingDb> options) : DbContext(options)
    {
        public const string Table = "gadgets_named_like_an_app_source";

        protected override void OnModelCreating(ModelBuilder model)
        {
            Gadgets(
                model,
                gadget => gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter())
            );
            model.Entity<Gadget>().ToTable(Table);
        }
    }

    [Fact]
    public void A_marked_converter_and_EF_s_built_ins_that_round_trip_are_trusted()
    {
        using var db = new TrustedDb(
            new DbContextOptionsBuilder().UseNpgsql(postgres.ConnectionString).Options
        );

        var dependencies = () => db.Set<Gadget>().AllDependencies();

        dependencies.Should().NotThrow();
    }

    // A provider maps some types with a mapping EF shares between providers (SQLite's TimeSpan), whose assembly is EF's,
    // so the check takes the provider from the context, which UseSluice names on the model.
    [Fact]
    public async Task A_UseSluice_model_names_its_provider_for_the_date_and_time_check()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        db.Model[ProviderNameConventionSetPlugin.Annotation].Should().Be(db.Database.ProviderName);
    }

    // Each provider's own mapping of the column, built without a server: PostgreSQL's timestamp, interval and time keep
    // microseconds with or without a size; MySQL's datetime, timestamp and time keep whole seconds without one. Each
    // row names every column it refuses.
    public static TheoryData<string, Func<DbContext>, string?> DateAndTimeColumns =>
        new()
        {
            { "PostgreSQL's default", () => new DefaultDateDb(Npgsql()), null },
            { "PostgreSQL with a precision of 6", () => new MicrosecondDateDb(Npgsql()), null },
            { "PostgreSQL's timestamp without a size", () => new TimestampDateDb(Npgsql()), null },
            {
                "PostgreSQL's timestamp without a size, on a model naming another provider",
                () => new ElsewhereTimestampDateDb(Npgsql()),
                "Gadget.Made (timestamp)"
            },
            { "MySQL's default", () => new DefaultDateDb(MySql()), null },
            { "MySQL with a precision of 6", () => new MicrosecondDateDb(MySql()), null },
            { "MySQL's datetime(6) by column type", () => new SizedDatetimeDateDb(MySql()), null },
            {
                "MySQL's timestamp without a size",
                () => new TimestampDateDb(MySql()),
                "Gadget.Made (timestamp)"
            },
            {
                "MySQL's datetime without a size",
                () => new DatetimeDateDb(MySql()),
                "Gadget.Made (datetime)"
            },
            {
                "PostgreSQL's time with time zone",
                () => new TimeOfDayDb(Npgsql()),
                "Gadget.Stamped (time with time zone)"
            },
            { "PostgreSQL's interval and time", () => new DefaultTimedDb(Npgsql()), null },
            {
                "PostgreSQL's interval with a precision of 3",
                () => new MillisecondTimedDb(Npgsql()),
                "Gadget.Lasts (interval(3))"
            },
            {
                "MySQL's time(6), its default for a TimeSpan, and its time for a TimeOnly",
                () => new DefaultTimedDb(MySql()),
                "Gadget.Opens (time)"
            },
            {
                "MySQL's time(6) by column type for a TimeOnly",
                () => new SizedTimeOfDayDb(MySql()),
                null
            },
            {
                "MySQL's TimeOnly with a precision of 6",
                () => new PreciseTimeOfDayDb(MySql()),
                null
            },
            {
                "MySQL's time without a size for a TimeSpan",
                () => new UnsizedTimedDb(MySql()),
                "Gadget.Lasts (time), Gadget.Opens (time)"
            },
        };

    [Theory]
    [MemberData(nameof(DateAndTimeColumns))]
    public void A_unique_date_or_time_is_refused_where_its_provider_stores_it_to_less_than_the_microsecond(
        string column,
        Func<DbContext> create,
        string? refused
    )
    {
        using var db = create();

        var dependencies = () => db.Set<Gadget>().AllDependencies();

        if (refused is not null)
        {
            dependencies
                .Should()
                .Throw<InvalidOperationException>(column)
                .WithMessage($"Sluice can't key {refused}: *microsecond*");
        }
        else
        {
            dependencies.Should().NotThrow(column);
        }
    }

    private static DbContextOptions Npgsql() =>
        new DbContextOptionsBuilder().UseNpgsql("Host=none").Options;

    private static DbContextOptions MySql() =>
        new DbContextOptionsBuilder().UseMySQL("Server=none").Options;

    // Providers the suite can't build a model for, by the name EF's provider checks compare, with the size the column
    // type carries: SQL Server's datetime takes no size, though EF writes the one the model asks for into its store
    // type; a type without a size is trusted only from a provider known to keep microseconds without one.
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "datetime", null, true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "datetime", 7, true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "smalldatetime", null, true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "datetime2", null, false)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "datetimeoffset", null, false)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "datetime2", 3, true)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "TEXT", null, false)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "timestamp", null, true)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "datetime", 6, false)]
    [InlineData("Another.Provider", "datetime", null, true)]
    [InlineData("Another.Provider", "timestamp", null, true)]
    [InlineData("Another.Provider", "timestamp", 6, false)]
    public void A_date_store_type_is_imprecise_unless_its_provider_keeps_microseconds(
        string provider,
        string storeTypeNameBase,
        int? size,
        bool imprecise
    ) => ModelSources.ImpreciseDate(provider, storeTypeNameBase, size).Should().Be(imprecise);

    // The size is read from the store type the table is made from, whatever the model asks for (SQLite keeps a
    // precision out of its TEXT), and only a date or time type's, never a character length.
    [Theory]
    [InlineData("datetime(6)", "datetime", 6)]
    [InlineData("timestamp(3) with time zone", "timestamp", 3)]
    [InlineData("TIMESTAMP(3)", "TIMESTAMP", 3)]
    [InlineData("datetime2", "datetime2", null)]
    [InlineData("TEXT", "TEXT", null)]
    [InlineData("varchar(50)", "varchar", null)]
    [InlineData("interval(3)", "interval", 3)]
    [InlineData("INTERVAL DAY(9) TO SECOND(0)", "INTERVAL DAY", 0)]
    [InlineData("INTERVAL DAY(9) TO SECOND", "INTERVAL DAY", null)]
    public void A_date_column_s_size_is_the_one_its_type_carries(
        string storeType,
        string storeTypeNameBase,
        int? size
    ) => ModelSources.SizeIn(storeType, storeTypeNameBase).Should().Be(size);

    // Durations and times of day where the suite doesn't build a model: SQL Server's time keeps 100 ns without a size.
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "time", null, false)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "time", 0, true)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "TEXT", null, false)]
    [InlineData("Another.Provider", "time", null, true)]
    [InlineData("Another.Provider", "time", 6, false)]
    public void A_time_store_type_is_imprecise_unless_its_provider_keeps_microseconds(
        string provider,
        string storeTypeNameBase,
        int? size,
        bool imprecise
    ) => ModelSources.ImpreciseTime(provider, storeTypeNameBase, size).Should().Be(imprecise);

    public readonly record struct GadgetNo(int Value);

    public enum Colour
    {
        Red,
        Blue,
    }

    public sealed class Gadget
    {
        public GadgetNo Id { get; set; }
        public int? ParentId { get; set; }
        public Colour Colour { get; set; }
        public long ShelfId { get; set; }
        public DateTime Made { get; set; }
        public DateTimeOffset Stamped { get; set; }
        public TimeSpan Lasts { get; set; }
        public TimeOnly Opens { get; set; }
        public Guid Tag { get; set; }
    }

    public sealed class Shelf
    {
        public long Id { get; set; }
    }

    [SluiceRoundTrips]
    public sealed class GadgetNoConverter()
        : ValueConverter<GadgetNo, int>(no => no.Value, value => new GadgetNo(value));

    private static void Gadgets(ModelBuilder model, Action<EntityTypeBuilder<Gadget>> map)
    {
        var gadget = model.Entity<Gadget>();
        gadget.ToTable("gadgets_that_do_not_exist");
        gadget
            .Ignore(g => g.ShelfId)
            .Ignore(g => g.Made)
            .Ignore(g => g.Stamped)
            .Ignore(g => g.Lasts)
            .Ignore(g => g.Opens)
            .Ignore(g => g.Tag);
        map(gadget);
    }

    private sealed class UntrustedKeyDb(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) =>
            Gadgets(
                model,
                gadget =>
                    gadget
                        .Property(g => g.Id)
                        .HasConversion(no => no.Value, value => new GadgetNo(value))
            );
    }

    private sealed class UntrustedForeignKeyDb(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) =>
            Gadgets(
                model,
                gadget =>
                {
                    gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter());
                    gadget.Property(g => g.ParentId).HasConversion(id => id, id => id);
                    gadget.HasOne<Parent>().WithMany().HasForeignKey(g => g.ParentId);
                }
            );
    }

    public sealed class Parent
    {
        public int Id { get; set; }
    }

    private sealed class UntrustedUniqueDb(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) =>
            Gadgets(
                model,
                gadget =>
                {
                    gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter());
                    gadget.Property(g => g.Colour).HasConversion<string>();
                    gadget.HasIndex(g => g.Colour).IsUnique();
                }
            );
    }

    // The database rounds what a save writes to the column's precision (SQL Server's datetime and smalldatetime, which
    // the model check refuses whatever their precision, too, as it does a date column). One type each, since EF caches
    // a model per context type.
    private abstract class DatedDb(DbContextOptions options) : DbContext(options)
    {
        protected abstract void Made(PropertyBuilder<DateTime> made);

        protected override void OnModelCreating(ModelBuilder model) =>
            Gadgets(
                model,
                gadget =>
                {
                    gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter());
                    Made(gadget.Property(g => g.Made));
                    gadget.HasIndex(g => g.Made).IsUnique();
                }
            );
    }

    private sealed class MillisecondDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) => made.HasPrecision(3);
    }

    private sealed class DateColumnDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) => made.HasColumnType("date");
    }

    private sealed class MicrosecondDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) => made.HasPrecision(6);
    }

    private sealed class DefaultDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) { }
    }

    // A unique duration and time of day, each in its provider's default column but for what Lasts sets on the
    // duration. One type each, since EF caches a model per context type.
    private abstract class TimedDb(DbContextOptions options) : DbContext(options)
    {
        protected virtual void Lasts(PropertyBuilder<TimeSpan> lasts) { }

        protected virtual void Opens(PropertyBuilder<TimeOnly> opens) { }

        protected override void OnModelCreating(ModelBuilder model) =>
            Gadgets(
                model,
                gadget =>
                {
                    gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter());
                    Lasts(gadget.Property(g => g.Lasts));
                    gadget.HasIndex(g => g.Lasts).IsUnique();
                    Opens(gadget.Property(g => g.Opens));
                    gadget.HasIndex(g => g.Opens).IsUnique();
                }
            );
    }

    private sealed class DefaultTimedDb(DbContextOptions options) : TimedDb(options);

    private sealed class SizedTimeOfDayDb(DbContextOptions options) : TimedDb(options)
    {
        protected override void Opens(PropertyBuilder<TimeOnly> opens) =>
            opens.HasColumnType("time(6)");
    }

    private sealed class PreciseTimeOfDayDb(DbContextOptions options) : TimedDb(options)
    {
        protected override void Opens(PropertyBuilder<TimeOnly> opens) => opens.HasPrecision(6);
    }

    private sealed class MillisecondTimedDb(DbContextOptions options) : TimedDb(options)
    {
        protected override void Lasts(PropertyBuilder<TimeSpan> lasts) => lasts.HasPrecision(3);
    }

    private sealed class UnsizedTimedDb(DbContextOptions options) : TimedDb(options)
    {
        protected override void Lasts(PropertyBuilder<TimeSpan> lasts) =>
            lasts.HasColumnType("time");
    }

    // A DateTimeOffset's date dropped by a time-of-day column.
    private sealed class TimeOfDayDb(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) =>
            Gadgets(
                model,
                gadget =>
                {
                    gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter());
                    gadget.Property(g => g.Stamped).HasColumnType("time with time zone");
                    gadget.HasIndex(g => g.Stamped).IsUnique();
                }
            );
    }

    private sealed class TimestampDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) =>
            made.HasColumnType("timestamp");
    }

    // PostgreSQL's unsized timestamp, on a model that names a provider Sluice doesn't know: the provider named on the
    // model, not the mapping's, decides, as UseSluice names the context's.
    private sealed class ElsewhereTimestampDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) =>
            made.HasColumnType("timestamp");

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.HasAnnotation(ProviderNameConventionSetPlugin.Annotation, "Another.Provider");
        }
    }

    // EF's own parser, which MySQL's provider uses, reads the 6 as a size, not a precision.
    private sealed class SizedDatetimeDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) =>
            made.HasColumnType("datetime(6)");
    }

    private sealed class DatetimeDateDb(DbContextOptions options) : DatedDb(options)
    {
        protected override void Made(PropertyBuilder<DateTime> made) =>
            made.HasColumnType("datetime");
    }

    // A typed id marked by its author; an enum stored as its number; a long key stored in an int column, which holds
    // every value the column can; a DateTime and a TimeSpan stored as ticks; a Guid stored as bytes.
    private sealed class TrustedDb(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var gadget = model.Entity<Gadget>();
            gadget.ToTable("gadgets_that_do_not_exist");
            gadget.Ignore(g => g.ParentId);
            gadget.Property(g => g.Id).HasConversion(new GadgetNoConverter());
            gadget.HasIndex(g => g.Colour).IsUnique();
            gadget.Property(g => g.ShelfId).HasConversion(new CastingConverter<long, int>());
            gadget.HasOne<Shelf>().WithMany().HasForeignKey(g => g.ShelfId);
            gadget.Property(g => g.Made).HasConversion(new DateTimeToTicksConverter());
            gadget.HasIndex(g => g.Made).IsUnique();
            gadget.Property(g => g.Lasts).HasConversion(new TimeSpanToTicksConverter());
            gadget.HasIndex(g => g.Lasts).IsUnique();
            gadget.Property(g => g.Tag).HasConversion(new GuidToBytesConverter());
            gadget.HasIndex(g => g.Tag).IsUnique();
            model.Entity<Shelf>().ToTable("shelves_that_do_not_exist");
            model
                .Entity<Shelf>()
                .Property(s => s.Id)
                .HasConversion(new CastingConverter<long, int>());
        }
    }
}
