using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>A tenant, which each ticket's <see cref="HelpTicket.TenantId"/> points at.</summary>
public sealed class HelpTenant
{
    public string Id { get; set; } = "";
}

public sealed class HelpTicket
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>Filtered by a virtual property of the context, which a derived context could give a body.</summary>
public sealed class HelpMacro
{
    public int Id { get; set; }
    public string? Desk { get; set; }
}

/// <summary>
/// Filtered through marked properties the configure can't trust: a virtual one, and a get-only auto-property. Each
/// filter is named, so a read can ignore the other.
/// </summary>
public sealed class HelpShelf
{
    public int Id { get; set; }
    public string? TenantId { get; set; }
}

/// <summary>Filtered by the tenant service the context holds.</summary>
public sealed class HelpNote
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";
}

/// <summary>Filtered by a property of the context with a body.</summary>
public sealed class HelpTag
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";
}

/// <summary>Filtered through a property of the context marked [SluiceContextState], over a scoped tenant service.</summary>
public sealed class HelpForm
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// Filtered through properties of the context marked [SluiceContextState] whose bodies fall back to the caller's
/// ambient tenant: one to null, one to throwing. Each filter is named, so a read can ignore the other.
/// </summary>
public sealed class HelpQueue
{
    public int Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>What the caller's ambient tenant was when the scope first made it.</summary>
public sealed class CallerSnapshot
{
    public string? Tenant { get; } = AmbientTenant.Current;
}

/// <summary>
/// A query expression interceptor that changes nothing, one instance for the run: EF builds an internal service
/// provider per distinct set of them.
/// </summary>
public sealed class QueryRewriter : IQueryExpressionInterceptor
{
    private QueryRewriter() { }

    public static QueryRewriter Instance { get; } = new();

    public Expression QueryCompilationStarting(
        Expression queryExpression,
        QueryExpressionEventData eventData
    ) => queryExpression;
}

/// <summary>
/// The request's tenant, set once the request is authenticated, as an app's user context is: by middleware, after the
/// context that holds it was made.
/// </summary>
public sealed class TenantContext
{
    public string? TenantId { get; private set; }

    public void Set(string tenantId) => TenantId = tenantId;
}

/// <summary>The current request's tenant.</summary>
public interface ITenantProvider
{
    public string? TenantId { get; }
}

/// <summary>
/// Reads the tenant from a static <see cref="AsyncLocal{T}"/>, as ASP.NET Core's <c>IHttpContextAccessor</c> reads the
/// current request, so it flows with the caller's execution context into whatever the caller awaits.
/// </summary>
public sealed class AmbientTenant : ITenantProvider
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public string? TenantId => Current;
}

/// <summary>
/// A helpdesk whose tickets are filtered by the context's tenant, as EF's docs write a tenant filter. Its databases
/// are <c>helpdesk_a</c> and <c>helpdesk_b</c>, beside the shop's, for database-per-tenant.
/// </summary>
public abstract class HelpdeskModel(DbContextOptions options) : DbContext(options)
{
    public const string A = "helpdesk_a";
    public const string B = "helpdesk_b";

    private static readonly SemaphoreSlim _created = new(1, 1);
    private static bool _done;

    /// <summary>The tenant the ticket filter reads.</summary>
    public string? TenantId { get; set; }

    /// <summary>The tenant again, through a property with a body.</summary>
    public string? TenantName => TenantId;

    /// <summary>Settable, but virtual, so a derived context may read it through a body.</summary>
    public virtual string? Desk { get; set; }

    public DbSet<HelpTicket> Tickets => Set<HelpTicket>();
    public DbSet<HelpTag> Tags => Set<HelpTag>();
    public DbSet<HelpMacro> Macros => Set<HelpMacro>();
    public DbSet<HelpShelf> Shelves => Set<HelpShelf>();

    /// <summary>Marked, but virtual, so a derived context may override its body to read anything.</summary>
    [SluiceContextState]
    public virtual string? ShelfTenant => TenantId;

    /// <summary>Marked, but an auto-property only the constructor sets.</summary>
    [SluiceContextState]
    public string? ShelfDesk { get; } = "front";

    /// <summary>The connection string of <paramref name="database"/> on the shop's server.</summary>
    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(PostgresFixture.Running.ConnectionString)
        {
            Database = database,
        }.ConnectionString;

    /// <summary>Creates both databases and their schema, once per run.</summary>
    public static async Task EnsureCreated()
    {
        await _created.WaitAsync();
        try
        {
            if (_done)
            {
                return;
            }

            await using var server = new NpgsqlConnection(PostgresFixture.Running.ConnectionString);
            await server.OpenAsync();
            foreach (var database in new[] { A, B })
            {
                await using (var create = new NpgsqlCommand($"create database {database}", server))
                {
                    await create.ExecuteNonQueryAsync();
                }

                await using var db = new HelpdeskDb(
                    new DbContextOptionsBuilder<HelpdeskDb>()
                        .UseNpgsql(ConnectionString(database))
                        .Options,
                    new AmbientTenant(),
                    new TenantContext()
                );
                await db.Database.EnsureCreatedAsync();
                db.AddRange(new HelpTenant { Id = "acme" }, new HelpTenant { Id = "globex" });
                await db.SaveChangesAsync();
            }

            _done = true;
        }
        finally
        {
            _created.Release();
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<HelpTicket>().HasOne<HelpTenant>().WithMany().HasForeignKey(t => t.TenantId);
        model.Entity<HelpTicket>().HasQueryFilter(ticket => ticket.TenantId == TenantId);
        model.Entity<HelpTag>().HasQueryFilter(tag => tag.TenantId == TenantName);
        model.Entity<HelpMacro>().HasQueryFilter(macro => macro.Desk == Desk);
        model
            .Entity<HelpShelf>()
            .HasQueryFilter("marked virtual", shelf => shelf.TenantId == ShelfTenant);
        model
            .Entity<HelpShelf>()
            .HasQueryFilter("marked get-only", shelf => shelf.TenantId == ShelfDesk);
    }
}

/// <summary>
/// Takes the request's tenant service in its constructor and copies its tenant into
/// <see cref="HelpdeskModel.TenantId"/>, as a multi-tenant app does; its notes are filtered through the service itself.
/// </summary>
public sealed class HelpdeskDb : HelpdeskModel
{
    private readonly TenantContext _tenantContext;

    public HelpdeskDb(
        DbContextOptions<HelpdeskDb> options,
        ITenantProvider tenants,
        TenantContext tenantContext
    )
        : base(options)
    {
        Tenants = tenants;
        TenantId = tenants.TenantId;
        _tenantContext = tenantContext;
    }

    public ITenantProvider Tenants { get; }

    // The tenant a configure reads as, which the desk filters prefer to the caller's.
    private string? _readAs;

    public DbSet<HelpNote> Notes => Set<HelpNote>();
    public DbSet<HelpForm> Forms => Set<HelpForm>();
    public DbSet<HelpQueue> Queues => Set<HelpQueue>();

    /// <summary>The tenant service's tenant, which only <c>read.Db</c>'s configure sets inside a compute.</summary>
    [SluiceContextState]
    private string? FormTenant => _tenantContext is { TenantId: { } tenant } ? tenant : null;

    /// <summary>The tenant the configure set, or else the caller's, as an app reading the request's user does.</summary>
    [SluiceContextState]
    private string? DeskTenant => _readAs ?? AmbientTenant.Current;

    /// <summary>As <see cref="DeskTenant"/>, but with no caller it throws, as reading a missing request does.</summary>
    [SluiceContextState]
    private string RequestTenant =>
        _readAs
        ?? AmbientTenant.Current
        ?? throw new InvalidOperationException("There's no request.");

    /// <summary>Reads as <paramref name="tenant"/>, whoever the caller is.</summary>
    public void ReadAs(string tenant) => _readAs = tenant;

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<HelpNote>().HasQueryFilter(note => note.TenantId == Tenants.TenantId);
        model.Entity<HelpForm>().HasQueryFilter(form => form.TenantId == FormTenant);
        model.Entity<HelpQueue>().HasQueryFilter("desk", queue => queue.TenantId == DeskTenant);
        model
            .Entity<HelpQueue>()
            .HasQueryFilter("request", queue => queue.TenantId == RequestTenant);
    }
}

/// <summary>A helpdesk context EF can pool: its constructor takes only its options.</summary>
public sealed class PooledHelpdeskDb(DbContextOptions<PooledHelpdeskDb> options)
    : HelpdeskModel(options);

/// <summary>
/// A helpdesk context EF can pool whose forms are filtered through a marked property over the tenant service its
/// constructor takes. The pool makes it from the root provider, so a leased one holds the root's service, not a
/// scope's.
/// </summary>
public sealed class PooledFormsDb(
    DbContextOptions<PooledFormsDb> options,
    TenantContext tenantContext
) : HelpdeskModel(options)
{
    public DbSet<HelpForm> Forms => Set<HelpForm>();

    /// <summary>The tenant service's tenant, which only <c>read.Db</c>'s configure sets inside a compute.</summary>
    [SluiceContextState]
    private string? FormTenant => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<HelpForm>().HasQueryFilter(form => form.TenantId == FormTenant);
    }
}

/// <summary>A helpdesk context whose own OnConfiguring adds a query expression interceptor, after the options'.</summary>
public sealed class ConfiguringHelpdeskDb(DbContextOptions<ConfiguringHelpdeskDb> options)
    : HelpdeskModel(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder builder) =>
        builder.AddInterceptors(QueryRewriter.Instance);
}

/// <summary>
/// EF's documented pattern for pooled contexts with a tenant: a scoped factory that leases from the pool and sets the
/// request's tenant on every lease, whatever the last lease left.
/// </summary>
public sealed class TenantLeases(IDbContextFactory<PooledHelpdeskDb> pool, ITenantProvider tenant)
{
    public PooledHelpdeskDb Lease()
    {
        var context = pool.CreateDbContext();
        context.TenantId = tenant.TenantId;
        return context;
    }
}

/// <summary>A helpdesk context EF can pool whose dispose throws once it has disposed.</summary>
public sealed class FailingDisposeHelpdeskDb(DbContextOptions<FailingDisposeHelpdeskDb> options)
    : HelpdeskModel(options)
{
    public const string Failure = "The helpdesk context's dispose failed.";

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        throw new InvalidOperationException(Failure);
    }
}

/// <summary>A helpdesk tenant whose tickets load lazily, through the lazy loader EF passes its constructor.</summary>
public sealed class LazyHelpTenant
{
    private readonly ILazyLoader? _loader;
    private List<HelpTicket>? _tickets;

    public LazyHelpTenant() { }

    public LazyHelpTenant(ILazyLoader loader) => _loader = loader;

    public string Id { get; set; } = "";

    public List<HelpTicket>? Tickets
    {
        get
        {
            _loader?.Load(this);
            return _tickets;
        }
        set => _tickets = value;
    }
}

/// <summary>
/// The helpdesk's tenants as entities that load lazily, on a context never configured with <c>UseSluice</c>, so no
/// analysis ever sees its model.
/// </summary>
public sealed class LazyHelpdeskDb(DbContextOptions<LazyHelpdeskDb> options) : DbContext(options)
{
    public DbSet<LazyHelpTenant> Tenants => Set<LazyHelpTenant>();

    /// <summary>A context on <see cref="HelpdeskModel.A"/>, without <c>UseSluice</c>.</summary>
    public static LazyHelpdeskDb Create() =>
        new(
            new DbContextOptionsBuilder<LazyHelpdeskDb>()
                .UseNpgsql(HelpdeskModel.ConnectionString(HelpdeskModel.A))
                .Options
        );

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<HelpTicket>().ToTable("Tickets");
        model
            .Entity<LazyHelpTenant>()
            .ToTable("HelpTenant")
            .HasMany(tenant => tenant.Tickets)
            .WithOne()
            .HasForeignKey(ticket => ticket.TenantId);
    }
}
