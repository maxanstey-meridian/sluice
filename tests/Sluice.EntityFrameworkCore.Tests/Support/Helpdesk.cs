using Microsoft.EntityFrameworkCore;
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

    public DbSet<HelpNote> Notes => Set<HelpNote>();
    public DbSet<HelpForm> Forms => Set<HelpForm>();

    /// <summary>The tenant service's tenant, which only <c>read.Db</c>'s configure sets inside a compute.</summary>
    [SluiceContextState]
    private string? FormTenant => _tenantContext is { TenantId: { } tenant } ? tenant : null;

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<HelpNote>().HasQueryFilter(note => note.TenantId == Tenants.TenantId);
        model.Entity<HelpForm>().HasQueryFilter(form => form.TenantId == FormTenant);
    }
}

/// <summary>A helpdesk context EF can pool: its constructor takes only its options.</summary>
public sealed class PooledHelpdeskDb(DbContextOptions<PooledHelpdeskDb> options)
    : HelpdeskModel(options);

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
