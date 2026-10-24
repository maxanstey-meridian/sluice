using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// Tenants: <c>read.Db(configure)</c> sets the compute's own context up from the compute's arguments, so a query filter
/// on the context's tenant works inside a compute; the context comes from a DI scope of the compute's own, so it can
/// take scoped services, and is made without the caller's async state, so it keeps nothing of the caller's request. A
/// pooled context is built, never leased. What Sluice can't check, it takes on trust, and these tests pin down where
/// that line is.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantTests : IAsyncLifetime
{
    private static readonly Query<string, List<string>> _tickets = new("helpdesk-tickets");
    private static readonly Query<string, List<string>> _forms = new("helpdesk-forms");
    private static readonly Query<string, List<string>> _queues = new("helpdesk-queues");

    public static TheoryData<string> Registrations => new(["AddDbContext", "scoped factory"]);

    public static TheoryData<string> Pools =>
        new(["AddDbContextPool", "AddPooledDbContextFactory", "hand-built factory"]);

    public Task InitializeAsync() => HelpdeskModel.EnsureCreated();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task A_configured_context_s_tenant_filter_gives_each_tenant_its_own_rows_whatever_the_caller_s_tenant(
        string registration
    )
    {
        await using var node = Node(registration);
        var acme = await AddTicket(node, HelpdeskModel.A, "acme");
        var globex = await AddTicket(node, HelpdeskModel.A, "globex");

        AmbientTenant.Current = "globex";
        var acmeTickets = await Tickets(node, "acme");
        AmbientTenant.Current = "acme";
        var globexTickets = await Tickets(node, "globex");

        acmeTickets.Should().Contain(acme).And.NotContain(globex);
        globexTickets.Should().Contain(globex).And.NotContain(acme);
        var later = await AddTicket(node, HelpdeskModel.A, "acme");
        (await Tickets(node, "acme"))
            .Should()
            .Contain(later, "the write evicted the cached tickets");
    }

    [Fact]
    public async Task Reading_through_Children_on_the_tenant_key_keeps_the_filter_and_is_evicted_only_by_that_tenant_s_writes()
    {
        await using var node = Node("AddDbContext");
        var computes = 0;

        Task<List<string>> AcmeTickets(SluiceReads r, CancellationToken ct)
        {
            computes++;
            return r.Children(
                    r.Db<HelpdeskDb>(db => db.TenantId = "acme").Tickets,
                    t => t.TenantId,
                    "acme"
                )
                .Select(t => t.Title)
                .ToListAsync(ct);
        }

        await node.Cache.GetOrSetAsync(_tickets, "acme", AcmeTickets);
        await AddTicket(node, HelpdeskModel.A, "globex");
        await node.Cache.GetOrSetAsync(_tickets, "acme", AcmeTickets);
        computes.Should().Be(1, "globex's write doesn't evict acme's tickets");
        await AddTicket(node, HelpdeskModel.A, "acme");
        await node.Cache.GetOrSetAsync(_tickets, "acme", AcmeTickets);
        computes.Should().Be(2);
    }

    [Fact]
    public async Task An_unconfigured_context_still_refuses_a_filter_on_its_state()
    {
        await using var node = Node("AddDbContext");

        var read = () =>
            node
                .Cache.GetOrSetAsync(
                    _tickets,
                    "acme",
                    (r, ct) => r.Db<HelpdeskDb>().Tickets.Select(t => t.Title).ToListAsync(ct)
                )
                .AsTask();

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "*HelpTicket's query filter reads the context's state (HelpdeskModel.TenantId)*"
                    + "read.Db<HelpdeskDb>(db => …)*"
            );
    }

    [Theory]
    [InlineData(
        "a service the context holds",
        "*HelpNote's query filter reads the context's state through HelpdeskDb.Tenants.TenantId, which read.Db's configure can't set*"
            + "Read the context's state from a settable field or auto-property of the context*"
            + "Or filter by hand: the filter has no name, so only IgnoreQueryFilters() ignores it*",
        false
    )]
    [InlineData(
        "a property with a body",
        "*HelpTag's query filter reads the context's state through HelpdeskModel.TenantName, which read.Db's configure can't set*"
            + "Read the context's state from a settable field or auto-property of the context*"
            + "If the property's body reads only what the compute's configure sets, such as a scoped tenant service, "
            + "mark it [SluiceContextState] (it mustn't be virtual) and set that state with "
            + "read.Db<HelpdeskDb>((db, services) => …)*"
            + "Or filter by hand: the filter has no name, so only IgnoreQueryFilters() ignores it*",
        true
    )]
    [InlineData(
        "a virtual property",
        "*HelpMacro's query filter reads the context's state through HelpdeskModel.Desk, which read.Db's configure can't set*"
            + "Read the context's state from a settable field or auto-property of the context*"
            + "Or filter by hand: the filter has no name, so only IgnoreQueryFilters() ignores it*",
        false
    )]
    [InlineData(
        "a marked virtual property",
        "*HelpShelf's query filter \"marked virtual\" reads the context's state through HelpdeskModel.ShelfTenant, which "
            + "read.Db's configure can't set*mark it [SluiceContextState] (it mustn't be virtual)*",
        true
    )]
    [InlineData(
        "a marked get-only property",
        "*HelpShelf's query filter \"marked get-only\" reads the context's state through HelpdeskModel.ShelfDesk, which "
            + "read.Db's configure can't set*",
        false
    )]
    public async Task A_filter_reading_state_a_configure_cannot_set_is_refused_even_on_a_configured_context(
        string through,
        string message,
        bool offersMark
    )
    {
        await using var node = Node("AddDbContext");
        var query = new Query<string, int>($"helpdesk-through-{through.Replace(' ', '-')}");

        var read = () =>
            node
                .Cache.GetOrSetAsync(
                    query,
                    "acme",
                    (r, ct) =>
                    {
                        var db = r.Db<HelpdeskDb>(db => db.TenantId = "acme");
                        return through switch
                        {
                            "a property with a body" => db.Tags.CountAsync(ct),
                            "a virtual property" => db.Macros.CountAsync(ct),
                            "a marked virtual property" => db
                                .Shelves.IgnoreQueryFilters(["marked get-only"])
                                .CountAsync(ct),
                            "a marked get-only property" => db
                                .Shelves.IgnoreQueryFilters(["marked virtual"])
                                .CountAsync(ct),
                            _ => db.Notes.CountAsync(ct),
                        };
                    }
                )
                .AsTask();

        // Marking is offered only where it can help: a property with a body, holding a plain value.
        var refused = await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(message);
        refused.Which.Message.Contains("[SluiceContextState]").Should().Be(offersMark);
    }

    // The compute's scope is the one its context was made in, so the configure sets the very tenant service the context
    // holds, and a filter reading it through a marked property gives each tenant its own rows, whatever the caller's.
    [Fact]
    public async Task A_filter_reading_a_scoped_service_through_a_marked_property_gives_each_tenant_its_own_rows_and_entries()
    {
        await using var node = Node("AddDbContext");
        var acme = await AddForm(node, "acme");
        var globex = await AddForm(node, "globex");
        var computes = 0;

        // A caller with a tenant of its own: the marked property reads only the service the configure set.
        AmbientTenant.Current = "acme";

        ValueTask<List<string>> Forms(string tenant) =>
            node.Cache.GetOrSetAsync(
                _forms,
                tenant,
                (r, ct) =>
                {
                    computes++;
                    return r.Db<HelpdeskDb>(
                            (db, services) =>
                                services.GetRequiredService<TenantContext>().Set(tenant)
                        )
                        .Forms.Select(f => f.Title)
                        .ToListAsync(ct);
                }
            );

        (await Forms("acme")).Should().Contain(acme).And.NotContain(globex);
        (await Forms("globex")).Should().Contain(globex).And.NotContain(acme);
        (await Forms("acme")).Should().Contain(acme);
        (await Forms("globex")).Should().Contain(globex);
        computes.Should().Be(2, "each tenant has its own entry, and the second reads were hits");
    }

    [Fact]
    public async Task A_filter_reading_a_marked_property_is_refused_on_an_unconfigured_context()
    {
        await using var node = Node("AddDbContext");

        var read = () =>
            node
                .Cache.GetOrSetAsync(
                    _forms,
                    "acme",
                    (r, ct) => r.Db<HelpdeskDb>().Forms.Select(f => f.Title).ToListAsync(ct)
                )
                .AsTask();

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "*HelpForm's query filter reads the context's state (HelpdeskDb.FormTenant)*"
                    + "read.Db<HelpdeskDb>((db, services) => …)*"
            );
    }

    [Fact]
    public async Task Configure_runs_once_when_the_context_is_made_and_later_calls_return_that_context()
    {
        await using var node = Node("AddDbContext");
        var configured = 0;
        HelpdeskDb? first = null;
        HelpdeskDb? later = null;

        await node.Cache.GetOrSetAsync(
            _tickets,
            "acme",
            async (r, ct) =>
            {
                first = r.Db<HelpdeskDb>(db =>
                {
                    configured++;
                    db.TenantId = "acme";
                });
                later = r.Db<HelpdeskDb>();
                return await later.Tickets.Select(t => t.Title).ToListAsync(ct);
            }
        );

        configured.Should().Be(1);
        later.Should().BeSameAs(first);
        later!.TenantId.Should().Be("acme");
    }

    [Theory]
    [InlineData("after the compute got it unconfigured")]
    [InlineData("after the compute configured it")]
    public async Task Configuring_a_context_the_compute_already_has_throws_and_caches_nothing(
        string when
    )
    {
        await using var node = Node("AddDbContext");
        var computes = 0;

        Task<List<string>> Compute(SluiceReads r, CancellationToken ct)
        {
            computes++;
            _ =
                when == "after the compute configured it"
                    ? r.Db<HelpdeskDb>(db => db.TenantId = "acme")
                    : r.Db<HelpdeskDb>();
            try
            {
                r.Db<HelpdeskDb>(db => db.TenantId = "globex");
            }
            catch (InvalidOperationException)
            {
                // Swallowing it doesn't help: the compute caches nothing, though it read something.
            }

            return r.Db<HelpdeskDb>().Set<HelpTenant>().Select(t => t.Id).ToListAsync(ct);
        }

        var read = () => node.Cache.GetOrSetAsync(_tickets, "acme", Compute).AsTask();

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "*read.Db<HelpdeskDb>(configure) was called after an earlier call had already made the compute's*"
            );
        await read.Should().ThrowAsync<InvalidOperationException>();
        computes.Should().Be(2, "the first call cached nothing");
    }

    // A task beside the configure that asks for the context waits for the configure, so it never gets the context
    // half set up.
    [Fact]
    public async Task A_task_asking_for_the_context_while_its_configure_runs_gets_it_configured()
    {
        await using var node = Node("AddDbContext");
        using var configuring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? seen = null;

        await node.Cache.GetOrSetAsync(
            _tickets,
            "acme",
            async (r, ct) =>
            {
                var configured = Task.Run(
                    () =>
                        r.Db<HelpdeskDb>(db =>
                        {
                            configuring.Set();
                            release.Wait(ct);
                            db.TenantId = "acme";
                        }),
                    ct
                );
                configuring.Wait(ct);
                var beside = Task.Run(() => seen = r.Db<HelpdeskDb>().TenantId, ct);
                (await Task.WhenAny(beside, Task.Delay(200, ct)))
                    .Should()
                    .NotBe(beside, "it waits for the configure");
                release.Set();
                var db = await configured;
                await beside;
                return await db.Tickets.Select(t => t.Title).ToListAsync(ct);
            }
        );

        seen.Should().Be("acme");
    }

    // A configure may ask for another context, such as a catalog to look the tenant's database up in. Both stay the
    // compute's own, and the first is configured once its configure returns.
    [Fact]
    public async Task A_configure_that_asks_for_another_context_leaves_both_the_compute_s_own()
    {
        await using var node = Node("AddDbContext");
        await AddTicket(node, HelpdeskModel.A, "acme");

        var tickets = await node.Cache.GetOrSetAsync(
            _tickets,
            "acme",
            async (r, ct) =>
            {
                var db = r.Db<HelpdeskDb>(db =>
                {
                    _ = r.Db<ShopDb>();
                    db.TenantId = "acme";
                });
                await r.Db<ShopDb>().Customers.CountAsync(ct);
                return await db.Tickets.Select(t => t.Title).ToListAsync(ct);
            }
        );

        tickets.Should().NotBeEmpty().And.OnlyContain(title => title.StartsWith("acme-"));
    }

    // A query on a context the compute already has doesn't wait for another context's configure.
    [Fact]
    public async Task A_query_on_another_context_runs_while_a_configure_is_still_running()
    {
        await using var node = Node("AddDbContext");
        using var configuring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        await node.Cache.GetOrSetAsync(
            _tickets,
            "acme",
            async (r, ct) =>
            {
                var shop = r.Db<ShopDb>();
                var configured = Task.Run(
                    () =>
                        r.Db<HelpdeskDb>(db =>
                        {
                            configuring.Set();
                            release.Wait(ct);
                            db.TenantId = "acme";
                        }),
                    ct
                );
                try
                {
                    configuring.Wait(ct);
                    await shop.Customers.CountAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
                finally
                {
                    release.Set();
                }

                return await (await configured).Tickets.Select(t => t.Title).ToListAsync(ct);
            }
        );
    }

    [Theory]
    [InlineData("db")]
    [InlineData("db, services")]
    public async Task A_configure_that_throws_reaches_the_caller_and_caches_nothing_even_if_the_compute_catches_it(
        string overload
    )
    {
        await using var node = Node("AddDbContext");
        var computes = 0;

        var read = () =>
            node
                .Cache.GetOrSetAsync(
                    _tickets,
                    "acme",
                    (r, ct) =>
                    {
                        computes++;
                        try
                        {
                            if (overload == "db")
                            {
                                r.Db<HelpdeskDb>(_ => throw new FormatException("bad tenant"));
                            }
                            else
                            {
                                r.Db<HelpdeskDb>((_, _) => throw new FormatException("bad tenant"));
                            }
                        }
                        catch (FormatException) { }

                        // Reads something, so only the failed configure stops it being cached.
                        return r.Db<HelpdeskDb>()
                            .Set<HelpTenant>()
                            .Select(t => t.Id)
                            .ToListAsync(ct);
                    }
                )
                .AsTask();

        await read.Should().ThrowAsync<FormatException>().WithMessage("bad tenant");
        await read.Should().ThrowAsync<FormatException>();
        computes.Should().Be(2);
    }

    // The compute runs on the first caller's flow, but its context, and the scoped services its constructor takes, are
    // made without that flow, so a constructor reading the request (IHttpContextAccessor, here an AsyncLocal) reads no
    // caller's tenant, whatever the caller is.
    [Fact]
    public async Task The_compute_s_context_is_made_without_the_caller_s_async_state()
    {
        await using var node = Node("AddDbContext");
        string? constructedWith = "unset";
        await AddTicket(node, HelpdeskModel.A, "acme");
        var globex = await AddTicket(node, HelpdeskModel.A, "globex");
        AmbientTenant.Current = "acme";

        var tickets = await node.Cache.GetOrSetAsync(
            _tickets,
            "globex",
            (r, ct) =>
                r.Db<HelpdeskDb>(db =>
                    {
                        constructedWith = db.TenantId;
                        db.TenantId = "globex";
                    })
                    .Tickets.Select(t => t.Title)
                    .ToListAsync(ct)
        );

        constructedWith.Should().BeNull("the constructor read no caller's ambient tenant");
        tickets.Should().Contain(globex).And.OnlyContain(title => title.StartsWith("globex-"));
    }

    // What Sluice takes on trust: a configure that sets something else leaves the tenant the constructor took, and
    // Sluice can't tell, so the rows of that tenant are cached under a key without one. Made without a caller, the
    // constructor took none, so no caller's rows are served to another.
    [Fact]
    public async Task A_configure_that_leaves_the_filter_s_state_alone_caches_what_a_context_made_without_a_caller_reads()
    {
        await using var node = Node("AddDbContext");
        await AddTicket(node, HelpdeskModel.A, "acme");
        var query = new Query<string, List<string>>("helpdesk-all-tickets");

        Task<List<string>> Compute(SluiceReads r, CancellationToken ct) =>
            r.Db<HelpdeskDb>(db => db.Database.SetCommandTimeout(30))
                .Tickets.Select(t => t.Title)
                .ToListAsync(ct);

        AmbientTenant.Current = "acme";
        var servedToAcme = await node.Cache.GetOrSetAsync(query, "all", Compute);
        AmbientTenant.Current = "globex";
        var servedToGlobex = await node.Cache.GetOrSetAsync(query, "all", Compute);

        servedToAcme.Should().BeEmpty("the context's tenant is null, which no ticket has");
        servedToGlobex.Should().BeEmpty();
    }

    // A marked property's body is the app's promise to read only what the configure sets. One that falls back to the
    // caller's request reads one value on the caller's flow and another without it, which Sluice sees.
    [Theory]
    [InlineData(
        "desk",
        "*HelpQueue's query filter \"desk\" reads [SluiceContextState] HelpdeskDb.DeskTenant, which gives a different "
            + "value with the caller's async state (IHttpContextAccessor, an AsyncLocal) than without it, so it reads "
            + "the caller rather than what read.Db's configure set*read.Db<HelpdeskDb>(db => …), or "
            + "(db, services) => … for a scoped service."
    )]
    [InlineData(
        "request",
        "*HelpQueue's query filter \"request\" reads [SluiceContextState] HelpdeskDb.RequestTenant, which throws "
            + "without the caller's async state (InvalidOperationException: There's no request.), so it reads the "
            + "caller*"
    )]
    public async Task A_marked_property_reading_the_caller_s_async_state_throws_unless_the_configure_set_it(
        string filter,
        string message
    )
    {
        await using var node = Node("AddDbContext");
        var acme = await AddQueue(node, "acme");
        var globex = await AddQueue(node, "globex");
        var other = filter == "desk" ? "request" : "desk";
        var computes = 0;

        ValueTask<List<string>> Queues(string key, Action<HelpdeskDb> configure) =>
            node.Cache.GetOrSetAsync(
                _queues,
                key,
                (r, ct) =>
                {
                    computes++;
                    return r.Db(configure)
                        .Queues.IgnoreQueryFilters([other])
                        .Select(q => q.Title)
                        .ToListAsync(ct);
                }
            );

        AmbientTenant.Current = "acme";
        var timeoutOnly = () => Queues("unset", db => db.Database.SetCommandTimeout(30)).AsTask();

        await timeoutOnly.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        await timeoutOnly.Should().ThrowAsync<InvalidOperationException>();
        computes.Should().Be(2, "nothing was cached");
        (await Queues("globex", db => db.ReadAs("globex")))
            .Should()
            .Contain(globex)
            .And.NotContain(acme);
    }

    [Fact]
    public async Task A_marked_property_reading_the_caller_s_async_state_runs_in_a_compute_with_no_caller()
    {
        await using var node = Node("AddDbContext");
        await AddQueue(node, "acme");

        var queues = await node.Cache.GetOrSetAsync(
            _queues,
            "nobody",
            (r, ct) =>
                r.Db<HelpdeskDb>(db => db.Database.SetCommandTimeout(30))
                    .Queues.IgnoreQueryFilters(["request"])
                    .Select(q => q.Title)
                    .ToListAsync(ct)
        );

        queues.Should().BeEmpty("with no caller the desk tenant is null, which no queue has");
    }

    // The services the configure asks for are made without the caller's async state, whichever way it asks, so one
    // that reads the request when it's made keeps no caller's.
    [Fact]
    public async Task A_scoped_service_the_configure_resolves_first_is_made_without_the_caller_s_async_state()
    {
        await using var node = Node("AddDbContext");
        List<string?> seen = [];
        AmbientTenant.Current = "acme";

        await node.Cache.GetOrSetAsync(
            _tickets,
            "acme",
            (r, ct) =>
                r.Db<HelpdeskDb>(
                        (db, services) =>
                        {
                            seen =
                            [
                                services.GetService<CallerSnapshot>()!.Tenant,
                                services.GetRequiredService<CallerSnapshot>().Tenant,
                                services.GetKeyedService<CallerSnapshot>("keyed")!.Tenant,
                                services.GetRequiredKeyedService<CallerSnapshot>("keyed").Tenant,
                            ];
                            db.TenantId = "acme";
                        }
                    )
                    .Tickets.Select(t => t.Title)
                    .ToListAsync(ct)
        );

        seen.Should().HaveCount(4).And.OnlyContain(tenant => tenant == null);
    }

    [Fact]
    public async Task A_context_with_a_scoped_dependency_from_the_default_singleton_factory_fails_saying_how_to_register_it()
    {
        await using var node = Node("singleton factory", validateScopes: true);

        var read = () => Tickets(node, "acme").AsTask();

        await read.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "*couldn't make a HelpdeskDb in the compute's own DI scope*scoped service*"
                    + "AddDbContextFactory<HelpdeskDb>(..., ServiceLifetime.Scoped)*"
            );
    }

    // Without scope validation (outside Development), the root provider makes the scoped service once and keeps it,
    // so every compute's context shares one.
    [Fact]
    public async Task Without_scope_validation_the_default_singleton_factory_gives_every_compute_one_tenant_service()
    {
        await using var node = Node("singleton factory");
        var services = new List<ITenantProvider>();

        foreach (var tenant in new[] { "acme", "globex" })
        {
            await node.Cache.GetOrSetAsync(
                _tickets,
                tenant,
                (r, ct) =>
                {
                    var db = r.Db<HelpdeskDb>(db => db.TenantId = tenant);
                    services.Add(db.Tenants);
                    return db.Tickets.Select(t => t.Title).ToListAsync(ct);
                }
            );
        }

        services[1].Should().BeSameAs(services[0]);
    }

    // EF resets a pooled context's own state between leases, not the app's fields, nor the connection string, so a
    // compute's context is built from the pool's options instead, and the pool's instance is left as it was.
    [Theory]
    [MemberData(nameof(Pools))]
    public async Task A_compute_s_context_never_comes_from_the_pool_so_the_next_lease_keeps_nothing_its_configure_set(
        string pool
    )
    {
        await using var node = Pool(pool);
        DbContextId pooled;
        await using (var request = node.Services.CreateAsyncScope())
        {
            pooled = request.ServiceProvider.GetRequiredService<PooledHelpdeskDb>().ContextId;
        }

        DbContextId computed = default;
        DbContextId leasedMeanwhile = default;
        await node.Cache.GetOrSetAsync(
            _tickets,
            "globex",
            async (r, ct) =>
            {
                var db = r.Db<PooledHelpdeskDb>(db =>
                {
                    db.TenantId = "globex";
                    db.Database.SetConnectionString(
                        HelpdeskModel.ConnectionString(HelpdeskModel.B)
                    );
                });
                computed = db.ContextId;
                await using (var request = node.Services.CreateAsyncScope())
                {
                    leasedMeanwhile = request
                        .ServiceProvider.GetRequiredService<PooledHelpdeskDb>()
                        .ContextId;
                }

                return await db.Tickets.Select(t => t.Title).ToListAsync(ct);
            }
        );
        await using var next = node.Services.CreateAsyncScope();
        var lease = next.ServiceProvider.GetRequiredService<PooledHelpdeskDb>();

        // A pool registered through EF isn't even leased. One the app built by hand is leased only to see that it's a
        // pool, and goes back untouched, after the request's lease, so the pool keeps the request's.
        if (pool == "hand-built factory")
        {
            leasedMeanwhile.InstanceId.Should().NotBe(pooled.InstanceId);
        }
        else
        {
            leasedMeanwhile.InstanceId.Should().Be(pooled.InstanceId);
        }

        lease
            .ContextId.InstanceId.Should()
            .Be(leasedMeanwhile.InstanceId, "the pool keeps one instance");
        computed.InstanceId.Should().NotBe(pooled.InstanceId).And.NotBe(leasedMeanwhile.InstanceId);
        lease.TenantId.Should().BeNull();
        lease.Database.GetConnectionString().Should().Contain(HelpdeskModel.A);
    }

    // A pooled context's constructor services come from the compute's scope, so the service the configure sets is the
    // one its marked property reads, not the one the pool made it with.
    [Fact]
    public async Task A_pooled_context_s_marked_property_reads_the_service_the_configure_set()
    {
        await using var node = new SluiceNode(services: services =>
        {
            services.AddScoped<TenantContext>();
            services.AddPooledDbContextFactory<PooledFormsDb>(
                (provider, options) =>
                    options
                        .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                        .UseSluice(provider),
                poolSize: 1
            );
        });
        var acme = await AddForm(node, "acme");
        var globex = await AddForm(node, "globex");

        ValueTask<List<string>> Forms(string tenant) =>
            node.Cache.GetOrSetAsync(
                _forms,
                tenant,
                (r, ct) =>
                    r.Db<PooledFormsDb>(
                            (db, services) =>
                                services.GetRequiredService<TenantContext>().Set(tenant)
                        )
                        .Forms.Select(f => f.Title)
                        .ToListAsync(ct)
            );

        (await Forms("acme")).Should().Contain(acme).And.NotContain(globex);
        (await Forms("globex")).Should().Contain(globex).And.NotContain(acme);
    }

    [Fact]
    public async Task With_EF_s_pattern_of_setting_the_tenant_on_every_lease_a_request_gets_its_own()
    {
        await using var node = PerLease();

        await Tickets(node, "globex", pooled: true);
        AmbientTenant.Current = "acme";
        await using var scope = node.Services.CreateAsyncScope();
        var request = scope.ServiceProvider.GetRequiredService<PooledHelpdeskDb>();

        request.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task Configure_can_point_the_context_at_a_tenant_s_own_database()
    {
        await using var node = Node("AddDbContext");
        var inA = await AddTicket(node, HelpdeskModel.A, "acme");
        var inB = await AddTicket(node, HelpdeskModel.B, "acme");

        var fromB = await node.Cache.GetOrSetAsync(
            _tickets,
            "acme@b",
            (r, ct) =>
                r.Db<HelpdeskDb>(db =>
                    {
                        db.Database.SetConnectionString(
                            HelpdeskModel.ConnectionString(HelpdeskModel.B)
                        );
                        db.TenantId = "acme";
                    })
                    .Tickets.Select(t => t.Title)
                    .ToListAsync(ct)
        );

        fromB.Should().Contain(inB).And.NotContain(inA);
    }

    // Dependencies are per entity type and key, not per database, so tenants in different databases share them.
    [Fact]
    public async Task A_write_in_one_tenant_s_database_evicts_reads_of_the_same_type_in_another_s()
    {
        await using var node = Node("AddDbContext");
        var computes = 0;

        Task<List<string>> ReadB(SluiceReads r, CancellationToken ct)
        {
            computes++;
            return r.Db<HelpdeskDb>(db =>
                {
                    db.Database.SetConnectionString(
                        HelpdeskModel.ConnectionString(HelpdeskModel.B)
                    );
                    db.TenantId = "globex";
                })
                .Tickets.Select(t => t.Title)
                .ToListAsync(ct);
        }

        await node.Cache.GetOrSetAsync(_tickets, "globex@b", ReadB);
        await AddTicket(node, HelpdeskModel.A, "acme");
        await node.Cache.GetOrSetAsync(_tickets, "globex@b", ReadB);

        computes.Should().Be(2, "a write in database a evicts a read of database b");
    }

    private static SluiceNode Node(string registration, bool validateScopes = false) =>
        new(
            services: services =>
            {
                services.AddScoped<ITenantProvider, AmbientTenant>();
                services.AddScoped<TenantContext>();
                services.AddTransient<CallerSnapshot>();
                services.AddKeyedTransient<CallerSnapshot>("keyed");
                var connection = HelpdeskModel.ConnectionString(HelpdeskModel.A);
                switch (registration)
                {
                    case "AddDbContext":
                        services.AddDbContext<HelpdeskDb>(
                            (provider, options) => options.UseNpgsql(connection).UseSluice(provider)
                        );
                        break;
                    case "scoped factory":
                        services.AddDbContextFactory<HelpdeskDb>(
                            (provider, options) =>
                                options.UseNpgsql(connection).UseSluice(provider),
                            ServiceLifetime.Scoped
                        );
                        break;
                    case "singleton factory":
                        services.AddDbContextFactory<HelpdeskDb>(
                            (provider, options) => options.UseNpgsql(connection).UseSluice(provider)
                        );
                        break;
                }
            },
            validateScopes: validateScopes
        );

    // A pool of one, registered each way an app can, so a lease after the compute is the pool's one instance.
    private static SluiceNode Pool(string pool) =>
        new(services: services =>
        {
            var connection = HelpdeskModel.ConnectionString(HelpdeskModel.A);
            switch (pool)
            {
                case "AddDbContextPool":
                    services.AddDbContextPool<PooledHelpdeskDb>(
                        (provider, options) => options.UseNpgsql(connection).UseSluice(provider),
                        poolSize: 1
                    );
                    break;
                case "AddPooledDbContextFactory":
                    services.AddPooledDbContextFactory<PooledHelpdeskDb>(
                        (provider, options) => options.UseNpgsql(connection).UseSluice(provider),
                        poolSize: 1
                    );
                    break;
                case "hand-built factory":
                    services.AddSingleton<IDbContextFactory<PooledHelpdeskDb>>(
                        provider => new PooledDbContextFactory<PooledHelpdeskDb>(
                            new DbContextOptionsBuilder<PooledHelpdeskDb>()
                                .UseNpgsql(connection)
                                .UseSluice(provider)
                                .Options,
                            poolSize: 1
                        )
                    );
                    services.AddScoped(provider =>
                        provider
                            .GetRequiredService<IDbContextFactory<PooledHelpdeskDb>>()
                            .CreateDbContext()
                    );
                    break;
            }
        });

    // A pool of one, with EF's tenant-per-lease pattern.
    private static SluiceNode PerLease() =>
        new(services: services =>
        {
            services.AddScoped<ITenantProvider, AmbientTenant>();
            services.AddPooledDbContextFactory<PooledHelpdeskDb>(
                (provider, options) =>
                    options
                        .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                        .UseSluice(provider),
                poolSize: 1
            );
            services.AddScoped<TenantLeases>();
            services.AddScoped(provider => provider.GetRequiredService<TenantLeases>().Lease());
        });

    private static ValueTask<List<string>> Tickets(
        SluiceNode node,
        string tenant,
        bool pooled = false
    ) =>
        node.Cache.GetOrSetAsync(
            _tickets,
            tenant,
            (r, ct) =>
                pooled
                    ? r.Db<PooledHelpdeskDb>(db => db.TenantId = tenant)
                        .Tickets.Select(t => t.Title)
                        .ToListAsync(ct)
                    : r.Db<HelpdeskDb>(db => db.TenantId = tenant)
                        .Tickets.Select(t => t.Title)
                        .ToListAsync(ct)
        );

    // Saved through a context with UseSluice, so its write invalidates the node's cache.
    private static async Task<string> AddForm(SluiceNode node, string tenant)
    {
        var title = $"{tenant}-form-{Guid.NewGuid():N}";
        await using var db = new HelpdeskDb(
            new DbContextOptionsBuilder<HelpdeskDb>()
                .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                .UseSluice(node.Services)
                .Options,
            new AmbientTenant(),
            new TenantContext()
        );
        db.Forms.Add(new HelpForm { TenantId = tenant, Title = title });
        await db.SaveChangesAsync();
        return title;
    }

    // Saved through a context with UseSluice, so its write invalidates the node's cache.
    private static async Task<string> AddQueue(SluiceNode node, string tenant)
    {
        var title = $"{tenant}-queue-{Guid.NewGuid():N}";
        await using var db = new HelpdeskDb(
            new DbContextOptionsBuilder<HelpdeskDb>()
                .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                .UseSluice(node.Services)
                .Options,
            new AmbientTenant(),
            new TenantContext()
        );
        db.Queues.Add(new HelpQueue { TenantId = tenant, Title = title });
        await db.SaveChangesAsync();
        return title;
    }

    // Saved through a context with UseSluice, so its write invalidates the node's cache.
    private static async Task<string> AddTicket(SluiceNode node, string database, string tenant)
    {
        var title = $"{tenant}-{Guid.NewGuid():N}";
        await using var db = new HelpdeskDb(
            new DbContextOptionsBuilder<HelpdeskDb>()
                .UseNpgsql(HelpdeskModel.ConnectionString(database))
                .UseSluice(node.Services)
                .Options,
            new AmbientTenant(),
            new TenantContext()
        );
        db.Tickets.Add(new HelpTicket { TenantId = tenant, Title = title });
        await db.SaveChangesAsync();
        return title;
    }
}
