using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Makes a compute's EF reads go through the read helpers, so every row it reads is covered by a recorded
/// dependency. The helpers mark their queries with <see cref="Marker"/>. Inside a compute, a write throws, and so does
/// a command without the marker (the origin check: forgotten reads, lazy and explicit loading, <c>FromSql</c>,
/// compiled queries), and a marked one, or a read a fused fetch runs, inside a transaction. A marked query that reads
/// anything beyond its own rows (another set, a navigation, <c>Include</c>, a query filter that does either), tracks,
/// or returns entities that load lazily throws when EF compiles it, inside a compute or not (the shape check).
/// </summary>
/// <remarks>
/// One instance for the whole process, separate from <see cref="SluiceInterceptor"/>, which is one per cache: EF
/// treats a query expression interceptor as a singleton interceptor and builds an internal service provider (with
/// its own model and compiled-query cache) per distinct set of them, throwing after twenty. Nothing here depends on
/// the cache. Each violation is also recorded on the running compute, so a compute that catches it still caches
/// nothing and its caller still gets it. A fused <c>read.From(dependency, fetch)</c> fetch is a declared read, so the
/// origin check lets its reads, and those of tasks it starts, through, though not its writes; the other checks still
/// apply there.
/// </remarks>
internal sealed class StrictReads : IDbCommandInterceptor, IQueryExpressionInterceptor
{
    /// <summary>The tag the read helpers put on their queries, and so at the head of their SQL.</summary>
    internal const string Marker = "Sluice read helper";

    private const string MarkerComment = $"-- {Marker}";

    private StrictReads() { }

    internal static StrictReads Instance { get; } = new();

    /// <summary>
    /// The query every read helper starts from: marked, no-tracking, and without auto-included navigations. Refuses a
    /// context the checks don't run on, since a helper there would read with them silently off.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context wasn't configured with <c>UseSluice</c>.</exception>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    internal static IQueryable<T> Query<T>(DbSet<T> set)
        where T : class
    {
        IInfrastructure<IServiceProvider> context = set;
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

        _ = ContextTransaction.Connection(context);
        return set.TagWith(Marker).AsNoTracking().IgnoreAutoIncludes();
    }

    // EF creates a fresh command for every execution on every path (reader, scalar and non-query; sync and async),
    // so this one hook sees each before it runs, and before the connection opens.
    public DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        Check(result, eventData);
        return result;
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
        if (
            eventData.Context is { } context
            && MarkerSearch.Finds(queryExpression)
            && ShapeCheck.Violation(context.Model, queryExpression) is { } violation
        )
        {
            ActiveCompute.Violate(violation, static (violation, _) => violation);
            throw violation;
        }

        return queryExpression;
    }

    // Outside a compute, Violate returns without calling ViolationOf, so the app's own commands pay nothing.
    private static void Check(DbCommand command, CommandEventData eventData)
    {
        if (ActiveCompute.Violate((command, eventData), ViolationOf) is { } violation)
        {
            throw violation;
        }
    }

    // A write, anywhere in a compute, whoever built it. A read a helper didn't build, unless it runs in a fused fetch,
    // whose declared dependencies cover it. Or any read the compute may make (a helper's, or one in a fetch) inside a
    // transaction, where a compute would cache rows other callers can't see yet, or an old snapshot under
    // RepeatableRead or Serializable. The transaction is read as the write side reads it, from EF's own inputs: the
    // command's own transaction would miss an enlisted or ambient one, which the connection carries instead.
    private static InvalidOperationException? ViolationOf(
        (DbCommand Command, CommandEventData Event) execution,
        bool inFetch
    )
    {
        var (command, eventData) = execution;
        if (IsWrite(eventData.CommandSource))
        {
            return new(
                $"An EF write ran inside a compute ({Statement(command)}). Writes can't run inside a compute, "
                    + "which is a read: run them before or after GetOrSetAsync."
            );
        }

        if (!inFetch && !command.CommandText.Contains(MarkerComment, StringComparison.Ordinal))
        {
            return NotFromHelper(command);
        }

        if (eventData.Context is { } context && !ContextTransaction.Of(context).IsNone)
        {
            return new(
                "Sluice's EF reads can't run inside a transaction: the compute would cache rows other callers can't "
                    + "see yet, or an old snapshot. Compute the value outside the transaction, or after it commits."
            );
        }

        return null;
    }

    // EF 10 reports ExecuteDelete's command as ExecuteUpdate; ExecuteDelete is listed for when it reports its own.
    private static bool IsWrite(CommandSource source) =>
        source
            is CommandSource.SaveChanges
                or CommandSource.ExecuteSqlRaw
                or CommandSource.ExecuteUpdate
                or CommandSource.ExecuteDelete;

    private static InvalidOperationException NotFromHelper(DbCommand command) =>
        new(
            $"An EF command that wasn't built by a Sluice read helper ran inside a compute ({Statement(command)}), "
                + "so nothing recorded what it read and no write would invalidate the cached value. Read through "
                + "read.Entity, read.Children or read.All, or run it in the fetch of read.From(dependency, fetch) "
                + "with a declared Source. Lazy and explicit loading, FromSql and compiled queries can't run "
                + "anywhere else inside a compute."
        );

    // The command's first line after its tags, so the message says which command it was. A LINQ query's captured
    // values go to the database as parameters and don't show, but constants inlined in a query and the text of raw
    // SQL do.
    private static string Statement(DbCommand command)
    {
        foreach (var line in command.CommandText.AsSpan().EnumerateLines())
        {
            var statement = line.Trim();
            if (!statement.IsEmpty && !statement.StartsWith("--"))
            {
                return statement.Length > 120 ? $"{statement[..120]}…" : statement.ToString();
            }
        }

        return "an empty command";
    }

    /// <summary>Finds the marker, the only thing an unmarked tree (the app's own queries) pays for.</summary>
    private sealed class MarkerSearch : ExpressionVisitor
    {
        private bool _found;

        internal static bool Finds(Expression query)
        {
            var search = new MarkerSearch();
            search.Visit(query);
            return search._found;
        }

        public override Expression? Visit(Expression? node) => _found ? node : base.Visit(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (
                node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && node.Method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith)
                && node.Arguments[1] is ConstantExpression { Value: Marker }
            )
            {
                _found = true;
                return node;
            }

            return base.VisitMethodCall(node);
        }
    }

    /// <summary>
    /// Finds the first way a marked query reads beyond the helper's own rows, in the tree and in the query filters
    /// EF will add to it. Only ownership navigations are allowed: owned values (JSON columns included) are part of
    /// their owner's row, so the helper's dependency covers them.
    /// </summary>
    private sealed class ShapeCheck(IModel model) : ExpressionVisitor
    {
        private int _roots;
        private IEntityType? _root;
        private bool _ignoresFilters;
        private List<string>? _ignoredFilters;

        // The type whose query filter is being checked, if any.
        private IEntityType? _filter;
        private InvalidOperationException? _violation;

        internal static InvalidOperationException? Violation(IModel model, Expression query)
        {
            var check = new ShapeCheck(model);
            check.Visit(query);
            check.CheckFilters();
            check.CheckResult(query.Type);
            return check._violation;
        }

        public override Expression? Visit(Expression? node)
        {
            // A filter that reads the context (a tenant, the current user) gives each context its own rows, so one
            // cached value would be served to all of them. DbSet accesses in a filter are query roots by now.
            if (
                _filter is not null
                && node is not null
                && typeof(DbContext).IsAssignableFrom(node.Type)
            )
            {
                _violation ??= new(
                    $"A Sluice read helper's query gets {_filter.DisplayName()}'s query filter, which reads the "
                        + "context's state (such as a tenant or the current user), so its cached value would be served "
                        + "to every context, whatever that state. Call IgnoreQueryFilters() on the helper's query, "
                        + "filter by that value explicitly, and put it in the cache key."
                );
            }

            return base.Visit(node);
        }

        protected override Expression VisitExtension(Expression node)
        {
            // Every root reads rows. Collections of plain values (an inline array, a list parameter) aren't roots yet:
            // EF turns them into roots after this interceptor runs.
            if (node is QueryRootExpression root)
            {
                if (++_roots == 1)
                {
                    _root = (root as EntityQueryRootExpression)?.EntityType;
                }
                else
                {
                    // SqlQuery's is the only other kind EF has built by now.
                    var name = root is EntityQueryRootExpression entity
                        ? entity.EntityType.DisplayName()
                        : $"SqlQuery<{root.ElementType.Name}>";
                    Reject(
                        $"A Sluice read helper's query also reads {name} (a join, a subquery, a set operation, "
                            + "FromSql or SqlQuery), so only the helper's own dependency would be recorded. Read "
                            + "related rows with their own read helper call inside the compute."
                    );
                }
            }

            return base.VisitExtension(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            if (method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            {
                VisitQueryOperator(node);
            }
            else if (
                node.TryGetEFPropertyArguments(out var instance, out var name)
                || node.TryGetIndexerArguments(model, out instance, out name)
            )
            {
                CheckNavigation(instance.Type, name);
            }
            else if (model.FindDbFunction(method) is { IsBuiltIn: false } function)
            {
                Reject(
                    $"A Sluice read helper's query calls the database function {function.Name}, which can read "
                        + "other rows, so only the helper's own dependency would be recorded. Call it in the fetch of "
                        + "read.From(dependency, fetch) with a declared Source instead."
                );
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is not null && node.Member.DeclaringType is { } declaringType)
            {
                CheckNavigation(declaringType, node.Member.Name);
            }

            return base.VisitMember(node);
        }

        private void VisitQueryOperator(MethodCallExpression node)
        {
            switch (node.Method.Name)
            {
                case nameof(EntityFrameworkQueryableExtensions.Include)
                or nameof(EntityFrameworkQueryableExtensions.ThenInclude):
                    Reject(
                        "A Sluice read helper's query uses Include, which loads related rows without recording a "
                            + "dependency on them. Read related rows with their own read helper call inside the "
                            + "compute; owned types are loaded with their owner without it."
                    );
                    break;
                // AsTracking(behaviour) calls AsTracking(), AsNoTracking() or AsNoTrackingWithIdentityResolution()
                // before the tree is built, so this is only ever tracking. Identity resolution stays allowed: it
                // uses a tracker of its own, not the context's, so it can't hand back the caller's instances.
                case nameof(EntityFrameworkQueryableExtensions.AsTracking):
                    Reject(
                        "A Sluice read helper's query was made tracking, so the cache could store the context's "
                            + "live instances and their unsaved edits. Keep helper queries no-tracking, and load an "
                            + "entity on your own context to change it."
                    );
                    break;
                // As EF reads it, for the whole query: without names, every filter; with them, those named.
                case nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters):
                    if (
                        node.Arguments
                        is [_, ConstantExpression { Value: IEnumerable<string> names }]
                    )
                    {
                        (_ignoredFilters ??= []).AddRange(names);
                    }
                    else
                    {
                        _ignoresFilters = true;
                    }

                    break;
            }
        }

        // A member name that is a non-ownership navigation on any entity type the instance can be reads related rows.
        // That's every entity type whose CLR type is assignable to the member's declaring type: a derived type
        // reached through a cast, an interface in generic code, a property bag's indexer.
        private void CheckNavigation(Type declaringType, string name)
        {
            if (_violation is not null || declaringType.IsValueType)
            {
                return;
            }

            foreach (var entityType in model.GetEntityTypes())
            {
                if (!declaringType.IsAssignableFrom(entityType.ClrType))
                {
                    continue;
                }

                var navigation =
                    (INavigationBase?)entityType.FindNavigation(name)
                    ?? entityType.FindSkipNavigation(name);
                if (navigation is INavigation { ForeignKey.IsOwnership: true } or null)
                {
                    continue;
                }

                Reject(
                    $"A Sluice read helper's query uses the navigation {entityType.DisplayName()}.{name}, which reads "
                        + "related rows without recording a dependency on them. Read related rows with their own read "
                        + "helper call inside the compute; owned types are fine."
                );
                return;
            }
        }

        // EF adds the root type's query filters to the query after this interceptor has run, so they're checked as
        // part of it, unless the query ignores them. Only a root type can declare filters.
        private void CheckFilters()
        {
            if (_ignoresFilters || _root?.GetRootType() is not { } rootType)
            {
                return;
            }

            _filter = rootType;
            foreach (var filter in rootType.GetDeclaredQueryFilters())
            {
                if (
                    filter.Expression is { } predicate
                    && (filter.IsAnonymous || _ignoredFilters?.Contains(filter.Key) != true)
                )
                {
                    Visit(predicate.Body);
                }
            }

            _filter = null;
        }

        // A cached entity must stand alone: one that loads lazily would read related rows later, through its context,
        // outside any compute, and one that holds its context keeps that context, and all it tracks, in the cache.
        // Owned values are materialised with their owner, so an owner is refused for what it owns too. The result is
        // checked by type: the element of a sequence, a single result, and what those hold through generic arguments
        // (anonymous types and tuples included) and arrays. An entity inside a class of your own isn't seen; an
        // object-typed value is taken for one.
        private void CheckResult(Type result)
        {
            if (_root is null)
            {
                return;
            }

            List<(Type Type, string Message)> refused = [];
            foreach (var entityType in _root.GetDerivedTypesInclusive().SelectMany(WithOwned))
            {
                foreach (var part in WithOwned(entityType))
                {
                    if (Holds(part) is { } holds)
                    {
                        refused.Add(
                            (
                                entityType.ClrType,
                                $"A Sluice read helper's query returns {entityType.ShortName()} entities (including "
                                    + $"what they own), and {part.ShortName()} entities {holds}. Select the values you "
                                    + "need instead."
                            )
                        );
                        break;
                    }
                }
            }

            if (refused.Count > 0)
            {
                CheckResult(result, refused);
            }
        }

        private void CheckResult(Type type, List<(Type Type, string Message)> refused)
        {
            if (_violation is not null)
            {
                return;
            }

            if (
                refused.Find(entity => type.IsAssignableFrom(entity.Type)) is
                { Message: { } message }
            )
            {
                _violation = new(message);
                return;
            }

            // Anonymous types are generic in their members' types, as tuples are.
            foreach (
                var inner in type.IsArray ? [type.GetElementType()!] : type.GetGenericArguments()
            )
            {
                CheckResult(inner, refused);
            }
        }

        // The type and every type it owns, at any depth.
        private static IEnumerable<IEntityType> WithOwned(IEntityType entityType) =>
            entityType
                .GetNavigations()
                .Where(navigation =>
                    navigation is { ForeignKey.IsOwnership: true, IsOnDependent: false }
                )
                .SelectMany(navigation => WithOwned(navigation.TargetEntityType))
                .Prepend(entityType);

        // What EF injects into the type that a cached instance mustn't keep: a lazy loader (into a service property,
        // which is how lazy-loading proxies get theirs, or a constructor, as the loader or as its delegate, which has
        // no property) or the context itself.
        private static string? Holds(IEntityType entityType)
        {
            var services = entityType
                .GetServiceProperties()
                .Select(property => property.ParameterBinding.ServiceType)
                .Concat(
                    entityType
                        .ConstructorBinding?.ParameterBindings.OfType<ServiceParameterBinding>()
                        .Select(binding => binding.ServiceType)
                        ?? []
                );
            foreach (var service in services)
            {
                if (service == typeof(ILazyLoader))
                {
                    return "load their navigations lazily, so a cached one would read related rows later, outside "
                        + "the compute and with no dependency recorded";
                }

                if (typeof(DbContext).IsAssignableFrom(service))
                {
                    return "hold the context that loaded them, so a cached one would keep that context, and what it "
                        + "tracks, alive and shared by every caller";
                }
            }

            return null;
        }

        private void Reject(string message) =>
            _violation ??= new(
                _filter is null
                    ? message
                    : $"{message} It's in {_filter.DisplayName()}'s query filter, which EF adds to the query: call "
                        + "IgnoreQueryFilters() on the helper's query to leave it out, and filter explicitly."
            );
    }
}
