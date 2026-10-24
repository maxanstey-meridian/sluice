namespace Sluice;

/// <summary>
/// Marks a property of a <c>DbContext</c> whose body reads only state that <c>read.Db</c>'s configure sets, so a
/// query filter may read it inside a compute: <c>[SluiceContextState] private Guid? TenantId =&gt; _tenant.Id;</c>,
/// over a scoped tenant service the configure sets with
/// <c>read.Db&lt;AppDb&gt;((db, services) =&gt; services.GetRequiredService&lt;Tenant&gt;().Set(tenantId))</c>.
/// </summary>
/// <remarks>
/// It's your promise that the body reads only what the configure sets, from the compute's arguments, and Sluice checks
/// what it can of it. A marked property is treated as the context's own state, like a settable field: a filter reading
/// it runs only on a context the compute configured. Each time such a query runs, Sluice reads the property twice, on
/// the compute's flow, which is the first caller's, and again without the caller's async state, as the compute's
/// context and the services its configure asks for are made. A body that reads the caller's request
/// (<c>IHttpContextAccessor</c>, an <c>AsyncLocal</c>, <c>Activity.Current</c>, the current culture) gives two values,
/// or throws without one, and the query throws rather than cache that caller's rows for every caller with the key.
/// What Sluice can't see stays on trust: a body reading the clock, a static, or a service it resolves later on the
/// compute's flow caches one value for every caller. The property must hold a plain value (a primitive, an enum, a
/// string, or a struct of those, nullable or not), must have a body, and mustn't be virtual or abstract: a derived
/// context could give it another body. An interface's implementation is fine. The mark does nothing on an
/// auto-property: a settable one is state the configure can already set, and a get-only or init-only one is refused,
/// since it holds what the constructor set.
/// <para>
/// The configure's services are the compute's own scope, the one the context was made in, so the service the
/// configure sets is the one the context holds. That's so for a pooled registration too: the compute's context is
/// built from the pool's options with its constructor's services from that scope, never leased from the pool.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SluiceContextStateAttribute : Attribute;
