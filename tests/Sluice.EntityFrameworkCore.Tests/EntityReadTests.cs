using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class EntityReadTests(PostgresFixture postgres)
{
    private static readonly Source<string> _greetings = new("ef-reads-greetings");
    private static readonly Query<int, Customer?> _customer = new("ef-reads-customer");
    private static readonly Query<int, string> _dashboard = new("ef-reads-dashboard");
    private static readonly Query<int, User> _user = new("ef-reads-user");
    private static readonly Query<int, Invoice> _invoice = new("ef-reads-invoice");

    private async Task<int> AddCustomer(ShopDb db, string name)
    {
        var customer = new Customer { Name = name };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    [Fact]
    public async Task An_entity_read_returns_its_row_and_is_evicted_by_its_key_and_by_AllDependencies_only()
    {
        await using var db = postgres.CreateDb();
        var ada = await AddCustomer(db, "Ada");
        var bob = await AddCustomer(db, "Bob");
        await using var node = new SluiceNode();

        var read = await node.Cache.GetOrSetAsync(
            _customer,
            ada,
            (read, ct) => read.Entity(db.Customers, ada).SingleOrDefaultAsync(ct)
        );

        read!.Name.Should().Be("Ada");
        Task ReadAda(SluiceReads read, CancellationToken ct) =>
            read.Entity(db.Customers, ada).SingleAsync(ct);
        (await Eviction.IsEvictedBy(ReadAda, db.Customers.Dependency(ada))).Should().BeTrue();
        (await Eviction.IsEvictedBy(ReadAda, db.Customers.AllDependencies())).Should().BeTrue();
        (await Eviction.IsEvictedBy(ReadAda, db.Customers.Dependency(bob))).Should().BeFalse();
        (await Eviction.IsEvictedBy(ReadAda, db.Orders.AllDependencies())).Should().BeFalse();
    }

    [Fact]
    public async Task A_read_that_finds_nothing_still_depends_on_its_key()
    {
        await using var db = postgres.CreateDb();
        const int missing = -1;
        await using var node = new SluiceNode();

        var read = await node.Cache.GetOrSetAsync(
            _customer,
            missing,
            (read, ct) => read.Entity(db.Customers, missing).SingleOrDefaultAsync(ct)
        );

        read.Should().BeNull();
        (
            await Eviction.IsEvictedBy(
                (read, ct) => read.Entity(db.Customers, missing).SingleOrDefaultAsync(ct),
                db.Customers.Dependency(missing)
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task An_All_read_is_evicted_by_any_key_of_its_set_and_by_AllDependencies_but_not_by_another_set()
    {
        await using var db = postgres.CreateDb();
        var ada = await AddCustomer(db, "Ada");

        Task ReadAll(SluiceReads read, CancellationToken ct) =>
            read.All(db.Customers).ToListAsync(ct);

        (await Eviction.IsEvictedBy(ReadAll, db.Customers.Dependency(ada))).Should().BeTrue();
        (await Eviction.IsEvictedBy(ReadAll, db.Customers.Dependency(-1))).Should().BeTrue();
        (await Eviction.IsEvictedBy(ReadAll, db.Customers.AllDependencies())).Should().BeTrue();
        (await Eviction.IsEvictedBy(ReadAll, db.Orders.AllDependencies())).Should().BeFalse();
    }

    [Fact]
    public async Task An_EF_dependency_and_a_declared_one_invalidate_together_in_one_batch()
    {
        await using var db = postgres.CreateDb();
        var ada = await AddCustomer(db, "Ada");
        Dependency[] batch = [db.Customers.Dependency(ada), _greetings.For("ada")];

        (
            await Eviction.IsEvictedBy(
                (read, ct) => read.Entity(db.Customers, ada).SingleAsync(ct),
                batch
            )
        )
            .Should()
            .BeTrue();
        (
            await Eviction.IsEvictedBy(
                (read, _) => read.From(_greetings.For("ada"), _ => Task.FromResult("hi ada")),
                batch
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task A_compute_mixing_an_EF_read_with_a_declared_source_is_evicted_by_either_side()
    {
        await using var db = postgres.CreateDb();
        var ada = await AddCustomer(db, "Ada");
        await using var node = new SluiceNode();
        var runs = 0;

        Task<string> Dashboard() =>
            node
                .Cache.GetOrSetAsync(
                    _dashboard,
                    ada,
                    async (read, ct) =>
                    {
                        runs++;
                        var customer = await read.Entity(db.Customers, ada).SingleAsync(ct);
                        var greeting = await read.From(
                            _greetings.For("ada"),
                            _ => Task.FromResult("hello")
                        );
                        return $"{greeting} {customer.Name}";
                    }
                )
                .AsTask();

        (await Dashboard()).Should().Be("hello Ada");
        await node.Cache.InvalidateAsync(_greetings.For("ada"));
        await Dashboard();
        runs.Should().Be(2, "the declared source was invalidated");

        Task Mixed(SluiceReads read, CancellationToken ct) =>
            Task.WhenAll(
                read.Entity(db.Customers, ada).SingleAsync(ct),
                read.From(_greetings.For("ada"), _ => Task.FromResult("hello"))
            );
        (await Eviction.IsEvictedBy(Mixed, db.Customers.Dependency(ada))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Mixed, _greetings.For("ada"))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Mixed, _greetings.For("bob"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_strongly_typed_id_with_a_value_converter_reads_its_row_and_is_evicted_by_the_same_id()
    {
        await using var db = postgres.CreateDb();
        db.Users.AddRange(
            new User { Id = new UserId(7), Name = "Grace" },
            new User { Id = new UserId(8), Name = "Alan" }
        );
        await db.SaveChangesAsync();

        Task<User> ReadGrace(SluiceReads read, CancellationToken ct) =>
            read.Entity(db.Users, new UserId(7)).SingleAsync(ct);

        await using var node = new SluiceNode();
        var grace = await node.Cache.GetOrSetAsync(_user, 7, ReadGrace);
        grace.Name.Should().Be("Grace");
        (await Eviction.IsEvictedBy(ReadGrace, db.Users.Dependency(new UserId(7))))
            .Should()
            .BeTrue();
        (await Eviction.IsEvictedBy(ReadGrace, db.Users.Dependency(new UserId(8))))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task A_typed_id_without_an_equality_operator_reads_its_row_and_is_evicted_by_the_same_id()
    {
        await using var db = postgres.CreateDb();
        db.Invoices.AddRange(
            new Invoice { No = new InvoiceNo(41), Payee = "Ada" },
            new Invoice { No = new InvoiceNo(42), Payee = "Bob" }
        );
        await db.SaveChangesAsync();

        Task<Invoice> ReadInvoice(SluiceReads read, CancellationToken ct) =>
            read.Entity(db.Invoices, new InvoiceNo(41)).SingleAsync(ct);

        await using var node = new SluiceNode();
        var invoice = await node.Cache.GetOrSetAsync(_invoice, 41, ReadInvoice);
        invoice.Payee.Should().Be("Ada");
        (await Eviction.IsEvictedBy(ReadInvoice, db.Invoices.Dependency(new InvoiceNo(41))))
            .Should()
            .BeTrue();
        (await Eviction.IsEvictedBy(ReadInvoice, db.Invoices.Dependency(new InvoiceNo(42))))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task A_derived_type_shares_its_root_type_dependencies()
    {
        await using var db = postgres.CreateDb();
        var rex = new Dog { Name = "Rex", Fetches = true };
        db.Dogs.Add(rex);
        await db.SaveChangesAsync();

        (
            await Eviction.IsEvictedBy(
                (read, ct) => read.Entity(db.Dogs, rex.Id).SingleAsync(ct),
                db.Animals.Dependency(rex.Id)
            )
        )
            .Should()
            .BeTrue();
        (
            await Eviction.IsEvictedBy(
                (read, ct) => read.All(db.Animals).ToListAsync(ct),
                db.Dogs.Dependency(rex.Id)
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task Reads_do_not_track_so_an_unsaved_edit_on_the_callers_context_is_not_cached()
    {
        await using var db = postgres.CreateDb();
        var customer = new Customer { Name = "Ada" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Orders.Add(new Order { CustomerId = customer.Id, Item = "Tea" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var tracked = await db.Customers.SingleAsync(c => c.Id == customer.Id);
        tracked.Name = "Unsaved";
        await using var node = new SluiceNode();

        var cached = await node.Cache.GetOrSetAsync(
            _customer,
            customer.Id,
            async (read, ct) =>
            {
                await read.All(db.Customers).ToListAsync(ct);
                await read.Children(db.Orders, o => o.CustomerId, customer.Id).ToListAsync(ct);
                return await read.Entity(db.Customers, customer.Id).SingleOrDefaultAsync(ct);
            }
        );

        cached!.Name.Should().Be("Ada");
        cached.Should().NotBeSameAs(tracked);
        db.ChangeTracker.Entries().Should().ContainSingle("only the caller's own query tracked");
    }

    [Fact]
    public async Task Cancelling_the_caller_cancels_its_running_EF_query()
    {
        await using var db = postgres.CreateDb();
        var ada = await AddCustomer(db, "Ada");

        // Another connection holds the table, so the compute's query blocks until it's cancelled.
        await using var holder = postgres.CreateDb();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"Customers\" IN ACCESS EXCLUSIVE MODE"
        );

        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        var querying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = node
            .Cache.GetOrSetAsync(
                _customer,
                ada,
                (read, ct) =>
                {
                    var query = read.Entity(db.Customers, ada);
                    querying.SetResult();
                    return query.SingleOrDefaultAsync(ct);
                },
                caller.Token
            )
            .AsTask();

        await querying.Task;
        await caller.CancelAsync();

        // A bound, not a wait: an ignored token would block on the lock until the transaction ends.
        await call.Invoking(c => c.WaitAsync(TimeSpan.FromSeconds(30)))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }
}
