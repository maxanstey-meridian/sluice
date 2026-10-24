using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sluice.EntityFrameworkCore.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class EntityReadTests(PostgresFixture postgres)
{
    private static readonly Source<string> _greetings = new("ef-reads-greetings");
    private static readonly Source<int> _fetched = new("ef-reads-fetched");
    private static readonly Query<int, Customer?> _customer = new("ef-reads-customer");
    private static readonly Query<int, User> _user = new("ef-reads-user");
    private static readonly Query<int, Invoice> _invoice = new("ef-reads-invoice");

    [Fact]
    public async Task An_entity_read_returns_its_row_and_is_evicted_by_its_key_and_by_AllDependencies_only()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, bob) = await db.AddCustomers();

        var read = await node.Cache.GetOrSetAsync(
            _customer,
            ada,
            (read, ct) => read.Entity(read.Db<ShopDb>().Customers, ada).SingleOrDefaultAsync(ct)
        );

        read!.Name.Should().Be("Ada");
        Task ReadAda(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Customers, ada).SingleAsync(ct);
        (await CachedRead.IsEvictedBy(ReadAda, db.Customers.Dependency(ada))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadAda, db.Customers.AllDependencies())).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadAda, db.Customers.Dependency(bob))).Should().BeFalse();
        (await CachedRead.IsEvictedBy(ReadAda, db.Orders.AllDependencies())).Should().BeFalse();
    }

    [Fact]
    public async Task A_read_that_finds_nothing_still_depends_on_its_key()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        const int missing = -1;

        var read = await node.Cache.GetOrSetAsync(
            _customer,
            missing,
            (read, ct) => read.Entity(read.Db<ShopDb>().Customers, missing).SingleOrDefaultAsync(ct)
        );

        read.Should().BeNull();
        (
            await CachedRead.IsEvictedBy(
                (read, ct) =>
                    read.Entity(read.Db<ShopDb>().Customers, missing).SingleOrDefaultAsync(ct),
                db.Customers.Dependency(missing)
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task A_compute_mixing_an_EF_read_with_a_declared_source_is_evicted_by_either_side()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();

        Task Mixed(SluiceReads read, CancellationToken ct) =>
            Task.WhenAll(
                read.Entity(read.Db<ShopDb>().Customers, ada).SingleAsync(ct),
                read.From(_greetings.For("ada"), _ => Task.FromResult("hello"))
            );
        (await CachedRead.IsEvictedBy(Mixed, db.Customers.Dependency(ada))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(Mixed, _greetings.For("ada"))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(Mixed, _greetings.For("bob"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_strongly_typed_id_with_a_value_converter_reads_its_row_and_is_evicted_by_the_same_id()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Users.AddRange(
            new User { Id = new UserId(7), Name = "Grace" },
            new User { Id = new UserId(8), Name = "Alan" }
        );
        await db.SaveChangesAsync();

        Task<User> ReadGrace(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Users, new UserId(7)).SingleAsync(ct);

        var grace = await node.Cache.GetOrSetAsync(_user, 7, ReadGrace);
        grace.Name.Should().Be("Grace");
        (await CachedRead.IsEvictedBy(ReadGrace, db.Users.Dependency(new UserId(7))))
            .Should()
            .BeTrue();
        (await CachedRead.IsEvictedBy(ReadGrace, db.Users.Dependency(new UserId(8))))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task A_typed_id_without_an_equality_operator_reads_its_row_and_is_evicted_by_the_same_id()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Invoices.AddRange(
            new Invoice { No = new InvoiceNo(41), Payee = "Ada" },
            new Invoice { No = new InvoiceNo(42), Payee = "Bob" }
        );
        await db.SaveChangesAsync();

        Task<Invoice> ReadInvoice(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Invoices, new InvoiceNo(41)).SingleAsync(ct);

        var invoice = await node.Cache.GetOrSetAsync(_invoice, 41, ReadInvoice);
        invoice.Payee.Should().Be("Ada");
        (await CachedRead.IsEvictedBy(ReadInvoice, db.Invoices.Dependency(new InvoiceNo(41))))
            .Should()
            .BeTrue();
        (await CachedRead.IsEvictedBy(ReadInvoice, db.Invoices.Dependency(new InvoiceNo(42))))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task A_derived_type_shares_its_root_type_dependencies()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var rex = new Dog { Name = "Rex", Fetches = true };
        var circle = new Circle { Radius = 1 };
        db.AddRange(rex, circle);
        await db.SaveChangesAsync();

        (
            await CachedRead.IsEvictedBy(
                (read, ct) => read.Entity(read.Db<ShopDb>().Dogs, rex.Id).SingleAsync(ct),
                db.Animals.Dependency(rex.Id)
            )
        )
            .Should()
            .BeTrue();
        (
            await CachedRead.IsEvictedBy(
                (read, ct) => read.Db<ShopDb>().Animals.ToListAsync(ct),
                db.Dogs.Dependency(rex.Id)
            )
        )
            .Should()
            .BeTrue();
        (
            await CachedRead.IsEvictedBy(
                (read, ct) => read.Entity(read.Db<ShopDb>().Circles, circle.Id).SingleAsync(ct),
                db.Shapes.Dependency(circle.Id)
            )
        )
            .Should()
            .BeTrue("an abstract TPC root has no table, but each of its concrete types does");
        (
            await CachedRead.IsEvictedBy(
                (read, ct) => read.Db<ShopDb>().Shapes.ToListAsync(ct),
                db.Circles.AllDependencies()
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task Reads_do_not_track_so_an_unsaved_edit_on_the_callers_context_is_not_cached()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var customer = new Customer { Name = "Ada" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Orders.Add(new Order { CustomerId = customer.Id, Item = "Tea" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var tracked = await db.Customers.SingleAsync(c => c.Id == customer.Id);
        tracked.Name = "Unsaved";

        var cached = await node.Cache.GetOrSetAsync(
            _customer,
            customer.Id,
            async (read, ct) =>
            {
                await read.Db<ShopDb>().Customers.ToListAsync(ct);
                await read.Children(read.Db<ShopDb>().Orders, o => o.CustomerId, customer.Id)
                    .ToListAsync(ct);
                return await read.Entity(read.Db<ShopDb>().Customers, customer.Id)
                    .SingleOrDefaultAsync(ct);
            }
        );

        cached!.Name.Should().Be("Ada");
        cached.Should().NotBeSameAs(tracked);
        db.ChangeTracker.Entries().Should().ContainSingle("only the caller's own query tracked");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_factory_timeout_does_not_let_the_caller_reuse_its_context_while_the_compute_s_query_runs(
        bool soft
    )
    {
        // FusionCache times a zero timeout out at once: a hard one always, a soft one when fail-safe has a stale
        // value to serve instead.
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(options =>
            {
                if (soft)
                {
                    options.IsFailSafeEnabled = true;
                    options.FactorySoftTimeout = TimeSpan.Zero;
                }
                else
                {
                    options.FactoryHardTimeout = TimeSpan.Zero;
                }
            })
        );
        await using var db = postgres.CreateDb(node);
        var (ada, _) = await db.AddCustomers();
        Task<Customer?> ReadAda(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Customers, ada).SingleOrDefaultAsync(ct);
        if (soft)
        {
            await node.Cache.GetOrSetAsync(_customer, ada, ReadAda);
            await node.Fusion.ExpireAsync($"ef-reads-customer:{ada}");
        }

        // Another connection holds the table, so the compute's query blocks until it lets go.
        await using var holder = postgres.CreateDb();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"Customers\" IN ACCESS EXCLUSIVE MODE"
        );
        var querying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = node
            .Cache.GetOrSetAsync(
                _customer,
                ada,
                (read, ct) =>
                {
                    var query = ReadAda(read, ct);
                    querying.SetResult();
                    return query;
                }
            )
            .AsTask();

        // Without Sluice's pin FusionCache gives up on the compute at once, maybe before it starts, so wait for either.
        await Task.WhenAny(querying.Task, call);
        call.IsCompleted.Should()
            .BeFalse("the caller must not resume while its compute's query runs");

        await hold.RollbackAsync();
        (await call)!.Name.Should().Be("Ada");
        (await db.Customers.CountAsync(c => c.Id == ada))
            .Should()
            .Be(1, "the context is free again");
    }

    // As a write from another request, outside the running compute, whose writes would otherwise throw.
    private static Task AsAnotherRequest(Func<Task> write)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(write);
        }
    }

    // Each test makes its own pages, with slugs and a section no other test uses.
    private static string Fresh(string name) => $"{name}-{Guid.NewGuid():N}";

    private static Task<CachedRead> PageBySlug(SluiceCache cache, string slug) =>
        CachedRead.Of(
            cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug).SingleOrDefaultAsync(ct)
        );

    private static async Task<Page> AddPage(ShopDb db, string? slug, string title = "Draft")
    {
        var page = new Page
        {
            Slug = slug,
            Title = title,
            Section = Fresh("section"),
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    [Fact]
    public async Task A_unique_column_read_returns_its_row_and_is_evicted_by_a_write_to_that_row_only()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("welcome");
        var page = await AddPage(db, slug, "Welcome");
        var other = await AddPage(db, Fresh("other"));
        var query = new Query<string, Page?>(Fresh("ef-reads-page"));

        var read = await node.Cache.GetOrSetAsync(
            query,
            slug,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug).SingleOrDefaultAsync(ct)
        );
        read!.Title.Should().Be("Welcome");
        var cached = await PageBySlug(node.Cache, slug);

        other.Title = "Edited";
        await db.SaveChangesAsync();
        (await cached.WasEvicted()).Should().BeFalse("another row's write doesn't touch this slug");
        page.Title = "Edited";
        await db.SaveChangesAsync();
        (await cached.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task A_unique_column_read_that_finds_nothing_is_evicted_by_an_insert_with_that_value()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("later");
        var cached = await PageBySlug(node.Cache, slug);

        await AddPage(db, Fresh("unrelated"));
        (await cached.WasEvicted()).Should().BeFalse();
        await AddPage(db, slug);
        (await cached.WasEvicted()).Should().BeTrue();
    }

    [Fact]
    public async Task Changing_a_unique_value_evicts_the_readers_of_the_old_value_and_of_the_new()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (before, after) = (Fresh("before"), Fresh("after"));
        var page = await AddPage(db, before);
        var oldReaders = await PageBySlug(node.Cache, before);
        var newReaders = await PageBySlug(node.Cache, after);

        page.Slug = after;
        await db.SaveChangesAsync();

        (await oldReaders.WasEvicted()).Should().BeTrue("the row left that value");
        (await newReaders.WasEvicted()).Should().BeTrue("the row took that value");
    }

    [Fact]
    public async Task Deleting_a_row_evicts_its_readers_by_unique_column()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("gone");
        var page = await AddPage(db, slug);
        var cached = await PageBySlug(node.Cache, slug);

        db.Pages.Remove(page);
        await db.SaveChangesAsync();

        (await cached.WasEvicted()).Should().BeTrue();
    }

    // Update() of an object the context didn't load marks every column modified with its original equal to its
    // current value, so the value the row had is unknown, and every reader by the column is evicted.
    [Fact]
    public async Task A_row_saved_without_being_loaded_evicts_every_reader_by_its_unique_column()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var page = await AddPage(db, Fresh("stub"));
        var otherSlug = Fresh("bystander");
        await AddPage(db, otherSlug);
        var bystander = await PageBySlug(node.Cache, otherSlug);
        db.ChangeTracker.Clear();
        var detached = await db.Pages.AsNoTracking().SingleAsync(p => p.Id == page.Id);

        detached.Title = "Rewritten";
        db.Pages.Update(detached);
        await db.SaveChangesAsync();

        (await bystander.WasEvicted()).Should().BeTrue();
    }

    // Without a row version, the value a row had when it was loaded may not be the one the database holds when it's
    // saved, so a save that updates or deletes one evicts every reader by the column, and says so once.
    [Fact]
    public async Task Without_a_row_version_a_delete_evicts_every_reader_by_unique_column_and_logs_why()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (mine, theirs) = (Fresh("WH"), Fresh("WH"));
        var warehouse = new Warehouse { Code = theirs };
        db.Warehouses.AddRange(new Warehouse { Code = mine }, warehouse);
        await db.SaveChangesAsync();
        var cached = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Warehouses, w => w.Code, mine).SingleAsync(ct)
        );

        db.Warehouses.Add(new Warehouse { Code = Fresh("WH") });
        await db.SaveChangesAsync();
        (await cached.WasEvicted()).Should().BeFalse("an insert's value is the one it writes");
        db.Warehouses.Remove(warehouse);
        await db.SaveChangesAsync();

        (await cached.WasEvicted()).Should().BeTrue();
        node.Logs.GetSnapshot()
            .Where(record => record.Id.Id == 3)
            .Select(record => record.Message)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("read.Entity of it by a unique column")
            .And.Contain("Warehouse has no concurrency token");
    }

    // The read records the key of the row it found, so a write by key reaches it. Raw SQL leaves no trace of the value
    // a row took, so only the whole type's dependencies reach a reader of a value no row held.
    [Fact]
    public async Task A_unique_column_read_is_evicted_by_the_key_of_the_row_it_found_and_by_AllDependencies()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (slug, missing) = (Fresh("raw"), Fresh("missing"));
        var page = await AddPage(db, slug);
        var other = await AddPage(db, Fresh("other"));
        Task ReadIt(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug).SingleAsync(ct);
        Task ReadMissing(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, missing).SingleOrDefaultAsync(ct);

        (await CachedRead.IsEvictedBy(ReadIt, db.Pages.Dependency(page.Id))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadIt, db.Pages.Dependency(other.Id))).Should().BeFalse();
        (await CachedRead.IsEvictedBy(ReadIt, db.Pages.AllDependencies())).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadMissing, db.Pages.Dependency(page.Id)))
            .Should()
            .BeFalse("a read that found no row recorded no key");
        (await CachedRead.IsEvictedBy(ReadMissing, db.Pages.AllDependencies())).Should().BeTrue();
    }

    // A fused fetch may read on a context of its own, gone once the compute returns, so there the read depends on the
    // whole type rather than looking up the rows it found.
    [Fact]
    public async Task A_unique_column_read_in_a_fused_fetch_on_its_own_context_depends_on_the_whole_type()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("fetched");
        var other = await AddPage(db, Fresh("other"));
        await AddPage(db, slug);
        Task ReadIt(SluiceReads read, CancellationToken ct) =>
            read.From(
                _fetched.All,
                async fetchCt =>
                {
                    await using var own = postgres.CreateDb(node);
                    return await read.Entity(own.Pages, p => p.Slug, slug).SingleAsync(fetchCt);
                }
            );

        (await CachedRead.IsEvictedBy(ReadIt, db.Pages.Dependency(other.Id))).Should().BeTrue();
    }

    // The lookup ignores query filters, so a row they hide is recorded too: a write that brings it back reaches the
    // reader by its key.
    [Fact]
    public async Task A_unique_column_read_records_the_key_of_a_row_its_query_filters_hide()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("deleted");
        var form = new Form { Slug = slug, IsDeleted = true };
        db.Forms.Add(form);
        await db.SaveChangesAsync();
        var cached = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Forms, f => f.Slug, slug).SingleOrDefaultAsync(ct)
        );

        await db
            .Forms.IgnoreQueryFilters()
            .ForKey(form.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(f => f.IsDeleted, false));

        (await cached.WasEvicted()).Should().BeTrue("the form it couldn't see came back");
    }

    // The lookup runs once the compute returns, so the value can move to another row in between. That write publishes
    // the value after the compute started, which evicts the entry whatever the lookup found.
    [Fact]
    public async Task A_value_that_moves_between_the_read_and_its_key_lookup_still_evicts()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("moving");
        var (from, to) = (await AddPage(db, slug), await AddPage(db, Fresh("to")));
        var moved = false;

        var cached = await CachedRead.Of(
            node.Cache,
            async (read, ct) =>
            {
                await read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug).SingleAsync(ct);
                if (!moved)
                {
                    moved = true;
                    await AsAnotherRequest(async () =>
                    {
                        from.Slug = null;
                        await db.SaveChangesAsync();
                        to.Slug = slug;
                        await db.SaveChangesAsync();
                    });
                }
            }
        );

        (await cached.WasEvicted())
            .Should()
            .BeTrue("the move published the slug during the compute");
        (await cached.WasEvicted()).Should().BeFalse("the second compute cached as usual");
        await node.Cache.InvalidateAsync(db.Pages.Dependency(to.Id));
        (await cached.WasEvicted()).Should().BeTrue("it found the row that holds the slug now");
    }

    [Fact]
    public async Task A_key_lookup_that_fails_fails_the_compute_and_caches_nothing()
    {
        var failing = new FailKeyLookups();
        await using var node = new SluiceNode(services: services =>
            services.AddDbContextFactory<ShopDb>(
                (provider, options) =>
                    options
                        .UseNpgsql(PostgresFixture.Running.ConnectionString)
                        .UseSluice(provider)
                        .AddInterceptors(failing)
            )
        );
        await using var db = postgres.CreateDb(node);
        var slug = Fresh("failing");
        await AddPage(db, slug);
        var computes = 0;
        var query = new Query<string, int>(Fresh("ef-reads-lookup"));
        Task<int> Read() =>
            node
                .Cache.GetOrSetAsync(
                    query,
                    slug,
                    async (read, ct) =>
                    {
                        await read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, slug)
                            .SingleAsync(ct);
                        return ++computes;
                    }
                )
                .AsTask();

        await ((Func<Task>)Read)
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The key lookup failed.");
        failing.Fail = false;
        (await Read()).Should().Be(2, "nothing was cached");
        (await Read()).Should().Be(2);
    }

    // Rows outside a filtered unique index can share its column's value; each is recorded, and past 100 of them the
    // reader depends on every row of the type instead.
    [Fact]
    public async Task A_value_several_rows_share_records_each_of_them_and_past_100_every_row()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var (shared, crowded) = (Fresh("draft"), Fresh("crowded"));
        var drafts = Enumerable.Range(0, 2).Select(_ => new Form { Slug = shared }).ToList();
        db.Forms.AddRange(drafts);
        db.Forms.AddRange(Enumerable.Range(0, 101).Select(_ => new Form { Slug = crowded }));
        var bystander = new Form { Slug = Fresh("bystander") };
        db.Forms.Add(bystander);
        await db.SaveChangesAsync();
        Task ReadBy(string slug, SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Forms, f => f.Slug, slug).ToListAsync(ct);

        foreach (var draft in drafts)
        {
            (
                await CachedRead.IsEvictedBy(
                    (read, ct) => ReadBy(shared, read, ct),
                    db.Forms.Dependency(draft.Id)
                )
            )
                .Should()
                .BeTrue();
        }

        (
            await CachedRead.IsEvictedBy(
                (read, ct) => ReadBy(shared, read, ct),
                db.Forms.Dependency(bystander.Id)
            )
        )
            .Should()
            .BeFalse();
        (
            await CachedRead.IsEvictedBy(
                (read, ct) => ReadBy(crowded, read, ct),
                db.Forms.Dependency(bystander.Id)
            )
        )
            .Should()
            .BeTrue("past 100 rows the reader depends on the whole type");
    }

    // The database deletes the warehouse's bins without EF loading them, so their labels are unknown.
    [Fact]
    public async Task A_delete_that_cascades_evicts_every_reader_of_the_dependent_by_unique_column()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var warehouse = new Warehouse { Code = Fresh("WH") };
        var label = Fresh("shelf");
        db.Warehouses.Add(warehouse);
        db.Bins.Add(new Bin { WarehouseCode = warehouse.Code, Label = label });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var cached = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Bins, b => b.Label, label).SingleOrDefaultAsync(ct)
        );

        db.Warehouses.Remove(await db.Warehouses.SingleAsync(w => w.Id == warehouse.Id));
        await db.SaveChangesAsync();

        (await db.Bins.AnyAsync(b => b.Label == label)).Should().BeFalse("the database deleted it");
        (await cached.WasEvicted()).Should().BeTrue();
    }

    // A vehicle deleted by its key alone may be a truck, whose VIN the base type's row doesn't hold.
    [Fact]
    public async Task A_delete_through_the_base_type_evicts_every_reader_of_a_derived_type_s_unique_column()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var vin = Fresh("VIN");
        var truck = new Truck { Plate = Fresh("T"), Vin = vin };
        db.Vehicles.Add(truck);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var cached = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Entity(read.Db<ShopDb>().Set<Truck>(), t => t.Vin, vin)
                    .SingleOrDefaultAsync(ct)
        );

        db.Vehicles.Remove(new Vehicle { Id = truck.Id });
        await db.SaveChangesAsync();

        (await db.Set<Truck>().AnyAsync(t => t.Id == truck.Id)).Should().BeFalse();
        (await cached.WasEvicted()).Should().BeTrue();
    }

    // The converter stores an empty slug as null, which no read by value finds and no save publishes. With a row version
    // the save trusts the slug a page had, so both the value it takes and the one it leaves go through that rule.
    [Fact]
    public async Task A_unique_value_its_converter_stores_as_null_is_refused_and_saves_without_losing_invalidation()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var page = await AddPage(db, "");
        var slug = Fresh("named");

        var refused = () =>
            CachedRead.Of(
                node.Cache,
                (read, ct) => read.Entity(read.Db<ShopDb>().Pages, p => p.Slug, "").SingleAsync(ct)
            );
        await refused
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("The value converter of Page.Slug stores '' as null*");

        var named = await PageBySlug(node.Cache, slug);
        page.Slug = slug;
        await db.SaveChangesAsync();
        (await named.WasEvicted()).Should().BeTrue("the page took the slug");

        named = await PageBySlug(node.Cache, slug);
        page.Slug = "";
        await db.SaveChangesAsync();
        (await named.WasEvicted()).Should().BeTrue("the page gave the slug up");
        node.Logs.GetSnapshot().Should().NotContain(record => record.Level == LogLevel.Error);
    }

    // The same rule for a foreign key: a bin whose overflow code is stored as null points at no warehouse.
    [Fact]
    public async Task A_foreign_key_its_converter_stores_as_null_is_refused_and_saves_without_losing_invalidation()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var warehouse = new Warehouse { Code = Fresh("WH") };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        var bins = await CachedRead.Of(
            node.Cache,
            (read, ct) =>
                read.Children(read.Db<ShopDb>().Bins, b => b.WarehouseCode, warehouse.Code)
                    .ToListAsync(ct)
        );

        var refused = () =>
            CachedRead.Of(
                node.Cache,
                (read, ct) =>
                    read.Children(read.Db<ShopDb>().Bins, b => b.OverflowCode, "").ToListAsync(ct)
            );
        await refused
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("The value converter of Bin.OverflowCode stores '' as null*");

        db.Bins.Add(new Bin { WarehouseCode = warehouse.Code, OverflowCode = "" });
        await db.SaveChangesAsync();

        (await bins.WasEvicted()).Should().BeTrue("a bin joined the warehouse");
        node.Logs.GetSnapshot().Should().NotContain(record => record.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_unique_column_read_through_the_primary_key_is_an_entity_read()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        var page = await AddPage(db, Fresh("keyed"));

        Task ReadById(SluiceReads read, CancellationToken ct) =>
            read.Entity(read.Db<ShopDb>().Pages, p => p.Id, page.Id).SingleAsync(ct);

        (await CachedRead.IsEvictedBy(ReadById, db.Pages.Dependency(page.Id))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadById, db.Pages.Dependency(page.Id + 1)))
            .Should()
            .BeFalse();
    }

    // A warehouse with no region stores an empty one, which a read by "" would find, but no save reports a null.
    [Fact]
    public async Task A_unique_column_whose_converter_stores_null_as_a_value_is_refused()
    {
        await using var node = new SluiceNode();

        var read = () =>
            CachedRead.Of(
                node.Cache,
                (read, ct) =>
                    read.Entity(read.Db<ShopDb>().Warehouses, w => w.Region, "")
                        .SingleOrDefaultAsync(ct)
            );

        await read.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("Warehouse.Region's value converter stores null as a value*");
    }

    // The same for a foreign key: a crate with no return warehouse stores an empty code, a child of "" to a reader.
    [Fact]
    public async Task A_foreign_key_whose_converter_stores_null_as_a_value_is_refused()
    {
        await using var node = new SluiceNode();

        var read = () =>
            CachedRead.Of(
                node.Cache,
                (read, ct) =>
                    read.Children(read.Db<ShopDb>().Crates, c => c.ReturnCode, "").ToListAsync(ct)
            );

        await read.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("Crate.ReturnCode's value converter stores null as a value*");
    }

    [Fact]
    public async Task A_unique_column_read_is_refused_for_a_column_without_a_single_column_unique_index_or_key()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);

        Task<Page?> Read(Func<SluiceReads, IQueryable<Page>> query) =>
            node
                .Cache.GetOrSetAsync(
                    new Query<string, Page?>(Fresh("ef-reads-refused")),
                    "k",
                    (read, ct) => query(read).SingleOrDefaultAsync(ct)
                )
                .AsTask();

        await ((Func<Task>)(() => Read(r => r.Entity(r.Db<ShopDb>().Pages, p => p.Title, "x"))))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage(
                "*Page.Title isn't covered by a single-column unique index or alternate key*"
            );
        await ((Func<Task>)(() => Read(r => r.Entity(r.Db<ShopDb>().Pages, p => p.Section, "x"))))
            .Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*Page.Section is unique only together with other columns*");
        await ((Func<Task>)(() => Read(r => r.Entity(r.Db<ShopDb>().Pages, p => p.Slug, 7))))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*must be of type String*");
        await ((Func<Task>)(() => Read(r => r.Entity(r.Db<ShopDb>().Pages, p => p.Slug, null!))))
            .Should()
            .ThrowAsync<ArgumentNullException>();
        await (
            (Func<Task>)(() => Read(r => r.Entity(r.Db<ShopDb>().Pages, p => p.Title.Length, 1)))
        )
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*doesn't name a property of Page*");
    }
}
