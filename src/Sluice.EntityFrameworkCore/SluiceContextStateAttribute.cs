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
/// nullable or not) and mustn't be virtual, unless it's sealed: a derived context could give it another body.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SluiceContextStateAttribute : Attribute;
