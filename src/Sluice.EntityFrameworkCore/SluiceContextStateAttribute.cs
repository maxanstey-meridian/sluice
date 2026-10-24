namespace Sluice;

/// <summary>
/// Marks a property of a <c>DbContext</c> whose body reads only state that <c>read.Db</c>'s configure sets, so a
/// query filter may read it inside a compute: <c>[SluiceContextState] private Guid? TenantId =&gt; _tenant.Id;</c>,
/// over a scoped tenant service the configure sets with
/// <c>read.Db&lt;AppDb&gt;((db, services) =&gt; services.GetRequiredService&lt;Tenant&gt;().Set(tenantId))</c>.
/// </summary>
/// <remarks>
/// It's your promise, and Sluice doesn't check it: Sluice never reads the body. A marked property is treated as the
/// context's own state, like a settable field: a filter reading it runs only on a context the compute configured, and
/// the configure is trusted to set what the body reads, from the compute's arguments. A body that reads anything else
/// (the clock, a static, the request's user when the configure didn't set it) caches one caller's rows for every
/// caller with that key. The property must hold a plain value (a primitive, an enum, a string, or a struct of those,
/// nullable or not), must have a body, and mustn't be virtual or abstract: a derived context could give it another
/// body. An interface's implementation is fine. The mark does nothing on an auto-property: a settable one is state the
/// configure can already set, and a get-only or init-only one is refused, since it holds what the constructor set.
/// <para>
/// The configure's services are the compute's own scope. On a context <c>AddDbContext</c> or a scoped factory makes,
/// that's the scope the context was made in, so the service the configure sets is the one the context holds. A context
/// leased straight from <c>AddDbContextPool</c> was made by the pool and keeps whatever service an earlier lease gave
/// it: lease it through a scoped factory that hands it the scope's service, as EF documents for pooled tenants, or
/// point it at the scope's service in the configure too.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SluiceContextStateAttribute : Attribute;
