using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// Tenants: <c>read.Db(configure)</c> sets the compute's own context up from the compute's arguments, so a query filter
/// on the context's tenant works inside a compute; the context comes from a DI scope of the compute's own, so it can
/// take scoped services. What Sluice can't check, it takes on trust, and these tests pin down where that line is.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantTests : IAsyncLifetime
{
    private static readonly Query<string, List<string>> _tickets = new("helpdesk-tickets");
    private static readonly Query<string, List<string>> _forms = new("helpdesk-forms");

    public static TheoryData<string> Registrations => new(["AddDbContext", "scoped factory"]);

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
            + "Or filter by hand: the filter has no name, so only IgnoreQueryFilters() ignores it*"
    )]
    [InlineData(
        "a property with a body",
        "*HelpTag's query filter reads the context's state through HelpdeskModel.TenantName, which read.Db's configure can't set*"
            + "Read the context's state from a settable field or auto-property of the context*"
            + "If the property's body reads only what the compute's configure sets, such as a scoped tenant service, "
            + "mark it [SluiceContextState] (it mustn't be virtual) and set that state with "
            + "read.Db<HelpdeskDb>((db, services) => …)*"
            + "Or filter by hand: the filter has no name, so only IgnoreQueryFilters() ignores it*"
    )]
    [InlineData(
        "a virtual property",
        "*HelpMacro's query filter reads the context's state through HelpdeskModel.Desk, which read.Db's configure can't set*"
            + "Read the context's state from a settable field or auto-property of the context*"
            + "Or filter by hand: the filter has no name, so only IgnoreQueryFilters() ignores it*"
    )]
    public async Task A_filter_reading_state_a_configure_cannot_set_is_refused_even_on_a_configured_context(
        string through,
        string message
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
                            _ => db.Notes.CountAsync(ct),
                        };
                    }
                )
                .AsTask();

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
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

    // The compute runs on the first caller's flow, so a scoped service reading the request (IHttpContextAccessor, here
    // an AsyncLocal) reads that caller's tenant inside the compute, whatever the cache key says.
    [Fact]
    public async Task The_compute_s_context_is_made_on_the_first_caller_s_flow_so_its_scoped_services_see_that_caller_s_tenant()
    {
        await using var node = Node("AddDbContext");
        string? constructedWith = null;
        string? serviceSays = null;
        await AddTicket(node, HelpdeskModel.A, "acme");
        AmbientTenant.Current = "acme";

        var tickets = await node.Cache.GetOrSetAsync(
            _tickets,
            "globex",
            (r, ct) =>
            {
                var db = r.Db<HelpdeskDb>(db =>
                {
                    constructedWith = db.TenantId;
                    db.TenantId = "globex";
                });
                serviceSays = db.Tenants.TenantId;
                return db.Tickets.Select(t => t.Title).ToListAsync(ct);
            }
        );

        constructedWith.Should().Be("acme", "the constructor read the caller's ambient tenant");
        serviceSays.Should().Be("acme");
        tickets.Should().NotContain(title => title.StartsWith("acme-"), "configure overrode it");
    }

    // What Sluice takes on trust: a configure that sets something else leaves the tenant the constructor took from
    // the first caller, and Sluice can't tell, so that caller's rows are cached under a key without a tenant.
    [Fact]
    public async Task A_configure_that_leaves_the_filter_s_state_alone_caches_the_first_caller_s_tenant_for_everyone()
    {
        await using var node = Node("AddDbContext");
        var acme = await AddTicket(node, HelpdeskModel.A, "acme");
        var query = new Query<string, List<string>>("helpdesk-all-tickets");

        Task<List<string>> Compute(SluiceReads r, CancellationToken ct) =>
            r.Db<HelpdeskDb>(db => db.Database.SetCommandTimeout(30))
                .Tickets.Select(t => t.Title)
                .ToListAsync(ct);

        AmbientTenant.Current = "acme";
        await node.Cache.GetOrSetAsync(query, "all", Compute);
        AmbientTenant.Current = "globex";
        var servedToGlobex = await node.Cache.GetOrSetAsync(query, "all", Compute);

        servedToGlobex.Should().Contain(acme);
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

    // EF resets a pooled context's own state between leases, not the app's fields, nor the connection string.
    [Fact]
    public async Task A_pooled_context_keeps_what_a_compute_s_configure_set_for_its_next_lease()
    {
        await using var node = Pooled(leases: false);

        await node.Cache.GetOrSetAsync(
            _tickets,
            "globex",
            (r, ct) =>
                r.Db<PooledHelpdeskDb>(db =>
                    {
                        db.TenantId = "globex";
                        db.Database.SetConnectionString(
                            HelpdeskModel.ConnectionString(HelpdeskModel.B)
                        );
                    })
                    .Tickets.Select(t => t.Title)
                    .ToListAsync(ct)
        );
        await using var next = node
            .Services.GetRequiredService<IDbContextFactory<PooledHelpdeskDb>>()
            .CreateDbContext();

        next.TenantId.Should().Be("globex");
        next.Database.GetConnectionString().Should().Contain(HelpdeskModel.B);
    }

    [Fact]
    public async Task With_EF_s_pattern_of_setting_the_tenant_on_every_lease_a_request_gets_its_own()
    {
        await using var node = Pooled(leases: true);

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

    // A pool of one, so the next lease is the context the compute used; with leases, EF's tenant-per-lease pattern.
    private static SluiceNode Pooled(bool leases) =>
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
            if (leases)
            {
                services.AddScoped<TenantLeases>();
                services.AddScoped(provider => provider.GetRequiredService<TenantLeases>().Lease());
            }
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
