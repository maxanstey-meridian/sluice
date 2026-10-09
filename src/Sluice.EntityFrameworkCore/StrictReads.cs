using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Makes a compute's EF reads go through the read helpers, so every row it reads is covered by a recorded
/// dependency. The helpers mark their queries with <see cref="Marker"/>. Inside a compute, a command without the
/// marker throws (the origin check: forgotten reads, lazy and explicit loading, <c>FromSql</c>, compiled queries), and
/// so does a marked one inside a transaction. A marked query that reads anything beyond its own rows (another set, a
/// navigation, <c>Include</c>) or tracks throws when EF compiles it, inside a compute or not (the shape check).
/// </summary>
/// <remarks>
/// One instance for the whole process, separate from <see cref="SluiceInterceptor"/>, which is one per cache: EF
/// treats a query expression interceptor as a singleton interceptor and builds an internal service provider (with
/// its own model and compiled-query cache) per distinct set of them, throwing after twenty. Nothing here depends on
/// the cache. Each violation is also recorded on the running compute, so a compute that catches it still caches
/// nothing and its caller still gets it.
/// </remarks>
internal sealed class StrictReads : IDbCommandInterceptor, IQueryExpressionInterceptor
{
    /// <summary>The tag the read helpers put on their queries, and so at the head of their SQL.</summary>
    internal const string Marker = "Sluice read helper";

    private const string MarkerComment = $"-- {Marker}";

    private StrictReads() { }

    internal static StrictReads Instance { get; } = new();

    /// <summary>The query every read helper starts from: marked, no-tracking, and without auto-included navigations.</summary>
    internal static IQueryable<T> Query<T>(DbSet<T> set)
        where T : class => set.TagWith(Marker).AsNoTracking().IgnoreAutoIncludes();

    /// <summary>Refuses a context the checks don't run on, since a helper there would read with them silently off.</summary>
    /// <exception cref="InvalidOperationException">The context wasn't configured with <c>UseSluice</c>.</exception>
    internal static void EnsureOn(IInfrastructure<IServiceProvider> context)
    {
        var interceptors = context
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()
            ?.Interceptors;
        if (interceptors?.Contains(Instance) != true)
        {
            throw new InvalidOperationException(
                "Sluice's EF reads need UseSluice on the context: its interceptors check that a compute reads only "
                    + "through the read helpers, and without them the checks would be silently off. Add "
                    + ".UseSluice(services) where the context is configured."
            );
        }
    }

    /// <summary>The violation for a helper query that would run inside a transaction, when it's built or run.</summary>
    internal static InvalidOperationException InTransaction() =>
        new(
            "Sluice's EF reads can't run inside a transaction: the compute would cache rows other callers can't see "
                + "yet, or an old snapshot. Compute the value outside the transaction, or after it commits."
        );

    public InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result
    )
    {
        Check(command, eventData);
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        Check(command, eventData);
        return new(result);
    }

    public InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result
    )
    {
        Check(command, eventData);
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default
    )
    {
        Check(command, eventData);
        return new(result);
    }

    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result
    )
    {
        Check(command, eventData);
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Check(command, eventData);
        return new(result);
    }

    // Runs only when EF compiles a query, not on every execution: a tree it has compiled before comes from its
    // compiled-query cache. So this runs whether or not a compute is active (a tree first compiled outside one would
    // otherwise be served to one unchecked), and a tree it rejects is never cached. The marker is a constant in the
    // tree, so a marked query never shares a cache entry with an unmarked one of the same shape.
    public Expression QueryCompilationStarting(
        Expression queryExpression,
        QueryExpressionEventData eventData
    )
    {
        if (eventData.Context is { } context)
        {
            var shape = new ShapeCheck(context.Model);
            shape.Visit(queryExpression);
            if (shape is { Marked: true, Violation: { } violation })
            {
                ActiveCompute.Violate(() => violation);
                throw violation;
            }
        }

        return queryExpression;
    }

    // Outside a compute nothing throws: ActiveCompute.Violate builds a violation only inside one. What every command
    // pays is a search of its SQL for the marker, and a marked one a look at its transaction. That's read as the helpers
    // and the write side read it, from EF's own inputs: the command's own transaction would miss an enlisted or
    // ambient one, which the connection carries instead.
    private static void Check(DbCommand command, CommandEventData eventData)
    {
        var violation =
            !command.CommandText.Contains(MarkerComment, StringComparison.Ordinal)
                ? ActiveCompute.Violate(NotFromHelper)
            : eventData.Context is { } context && !ContextTransaction.Of(context).IsNone
                ? ActiveCompute.Violate(InTransaction)
            : null;

        if (violation is not null)
        {
            throw violation;
        }
    }

    private static InvalidOperationException NotFromHelper() =>
        new(
            "An EF query that wasn't built by a Sluice read helper ran inside a compute, so nothing recorded what it "
                + "read and no write would invalidate the cached value. Read through read.Entity, read.Children or "
                + "read.All, or through read.From with a declared Source. Lazy and explicit loading, FromSql, compiled "
                + "queries and writes can't run inside a compute."
        );

    /// <summary>
    /// Finds the marker and the first shape violation in one pass. Only ownership navigations are allowed: owned
    /// values (JSON columns included) are part of their owner's row, so the helper's dependency covers them.
    /// </summary>
    private sealed class ShapeCheck(IModel model) : ExpressionVisitor
    {
        private int _roots;

        internal bool Marked { get; private set; }

        internal InvalidOperationException? Violation { get; private set; }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && ++_roots > 1)
            {
                Violation ??= new(
                    $"A Sluice read helper's query also reads {root.EntityType.DisplayName()} (a join, a subquery, a set "
                        + "operation or FromSql), so only the helper's own dependency would be recorded. Read related "
                        + "rows with their own read helper call inside the compute."
                );
            }

            return base.VisitExtension(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            if (method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            {
                switch (method.Name)
                {
                    case nameof(EntityFrameworkQueryableExtensions.TagWith)
                        when node.Arguments[1] is ConstantExpression { Value: Marker }:
                        Marked = true;
                        break;
                    case nameof(EntityFrameworkQueryableExtensions.Include)
                    or nameof(EntityFrameworkQueryableExtensions.ThenInclude):
                        Violation ??= new(
                            "A Sluice read helper's query uses Include, which loads related rows without recording a "
                                + "dependency on them. Read related rows with their own read helper call inside the "
                                + "compute; owned types are loaded with their owner without it."
                        );
                        break;
                    // AsTracking(behaviour) calls AsTracking(), AsNoTracking() or AsNoTrackingWithIdentityResolution()
                    // before the tree is built, so this is only ever tracking. Identity resolution stays allowed: it
                    // uses a tracker of its own, not the context's, so it can't hand back the caller's instances.
                    case nameof(EntityFrameworkQueryableExtensions.AsTracking):
                        Violation ??= new(
                            "A Sluice read helper's query was made tracking, so the cache could store the context's "
                                + "live instances and their unsaved edits. Keep helper queries no-tracking, and load "
                                + "an entity on your own context to change it."
                        );
                        break;
                }
            }
            else if (
                method.DeclaringType == typeof(EF)
                && method.Name == nameof(EF.Property)
                && node.Arguments is [var instance, ConstantExpression { Value: string name }]
            )
            {
                CheckNavigation(instance, name);
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is { } instance)
            {
                CheckNavigation(instance, node.Member.Name);
            }

            return base.VisitMember(node);
        }

        // Any entity type of the instance's CLR type will do: a member name that is a non-ownership navigation on one
        // of them reads related rows. A cast (an interface in generic code, a derived type) is looked through.
        private void CheckNavigation(Expression instance, string name)
        {
            while (
                instance
                    is UnaryExpression
                    {
                        NodeType: ExpressionType.Convert or ExpressionType.TypeAs
                    } cast
            )
            {
                instance = cast.Operand;
            }

            foreach (var entityType in model.FindEntityTypes(instance.Type))
            {
                var navigation =
                    (INavigationBase?)entityType.FindNavigation(name)
                    ?? entityType.FindSkipNavigation(name);
                if (navigation is INavigation { ForeignKey.IsOwnership: true } or null)
                {
                    continue;
                }

                Violation ??= new(
                    $"A Sluice read helper's query uses the navigation {entityType.DisplayName()}.{name}, which reads "
                        + "related rows without recording a dependency on them. Read related rows with their own read "
                        + "helper call inside the compute; owned types are fine."
                );
                return;
            }
        }
    }
}
