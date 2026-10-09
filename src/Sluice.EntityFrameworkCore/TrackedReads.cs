using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Records what a compute's EF queries read. When EF compiles a query, this works out the entity types it reads (its
/// roots, the navigations and <c>Include</c>s it follows, auto-included navigations, and the query filters EF adds)
/// and tags the query with them, so its SQL names them every time it runs. Inside a compute, each command records a
/// whole-type dependency on every type its tag names, so a write to any of them evicts the value. A query whose reads
/// can't be worked out (raw SQL, a database function, a view or keyless type, a query filter that reads the context,
/// entities that load lazily) is tagged as such, and throws inside a compute instead. Inside a compute, every command
/// must also come from a context the compute got from <c>read.Db</c>, writes throw, and so do reads inside a
/// transaction.
/// </summary>
/// <remarks>
/// A read helper's query records its own, narrower dependency on its root type when it's called, so the tag marks
/// that type as covered unless the query reaches it again. A fused <c>read.From(dependency, fetch)</c> fetch is
/// a declared read: its commands may come from any context and record nothing, though writes and transactions are
/// still refused there. One instance for the whole process, separate from <see cref="SluiceInterceptor"/>, which is
/// one per cache: EF treats a query expression interceptor as a singleton interceptor and builds an internal service
/// provider (with its own model and compiled-query cache) per distinct set of them, throwing after twenty. Each
/// violation is also recorded on the running compute, so a compute that catches it still caches nothing and its
/// caller still gets it.
/// </remarks>
internal sealed partial class TrackedReads : IDbCommandInterceptor, IQueryExpressionInterceptor
{
    /// <summary>The tag the read helpers put on their queries.</summary>
    internal const string Marker = "Sluice read helper";

    private const string ReadsTag = "Sluice reads: ";
    private const string UntrackableTag = "Sluice can't track: ";
    private const string HelperSuffix = " (helper)";
    private const string Separator = "; ";
    private const string DependenciesAnnotation = "Sluice:TagDependencies";

    // Each query and reads tag that has logged reading whole types, so each logs once per process.
    private static readonly ConcurrentDictionary<(string Query, string Tag), bool> _loggedWhole =
        new();

    private TrackedReads() { }

    internal static TrackedReads Instance { get; } = new();

    /// <summary>Whether <paramref name="context"/> was configured with <c>UseSluice</c>.</summary>
    internal static bool IsOn(IInfrastructure<IServiceProvider> context) =>
        context
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()
            ?.Interceptors?.Contains(Instance) == true;

    /// <summary>
    /// The query every read helper starts from: marked, no-tracking, and without auto-included navigations. Refuses a
    /// context the checks don't run on, and one the running compute didn't get from <c>read.Db</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The context wasn't configured with <c>UseSluice</c>, or a compute is running and didn't get it from
    /// <c>read.Db</c>.
    /// </exception>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    internal static IQueryable<T> Query<T>(DbSet<T> set)
        where T : class
    {
        IInfrastructure<IServiceProvider> context = set;
        if (!IsOn(context))
        {
            throw NotOn();
        }

        _ = ContextTransaction.Connection(context);
        if (
            ActiveCompute.Reads is { } reads
            && !ActiveCompute.InFetch(reads)
            && !ActiveCompute.Holds(reads, context.GetService<ICurrentDbContext>().Context)
        )
        {
            throw ActiveCompute.Violate(reads, ForeignContext("a read helper's query"));
        }

        return set.TagWith(Marker).AsNoTracking().IgnoreAutoIncludes();
    }

    internal static InvalidOperationException NotOn() =>
        new(
            "Sluice's EF reads need UseSluice on the context: it records what a compute's queries read, and without "
                + "it they'd record nothing. Add .UseSluice(services) where the context is configured."
        );

    // Runs only when EF compiles a query, not on every execution: a tree it has compiled before comes from its
    // compiled-query cache, so the tag goes into the tree. It runs whether or not a compute is active, since a tree
    // compiled outside one is served to one too, and it never throws: only a compute refuses what can't be tracked.
    // The helper marker is a constant in the tree, so a helper's query never shares a cache entry with another.
    public Expression QueryCompilationStarting(
        Expression queryExpression,
        QueryExpressionEventData eventData
    ) =>
        eventData.Context is { } context
            ? Tagging.Tag(queryExpression, Analysis.Of(context.Model, queryExpression))
            : queryExpression;

    // EF creates a fresh command for every execution on every path (reader, scalar and non-query; sync and async),
    // so this one hook sees each before it runs, and before the connection opens. Outside a compute it costs one
    // lookup.
    public DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        if (ActiveCompute.Reads is { } reads && Read(reads, result, eventData) is { } violation)
        {
            throw ActiveCompute.Violate(reads, violation);
        }

        return result;
    }

    // A write, anywhere in a compute, whoever built it. Outside a fused fetch, a command from a context the compute
    // didn't get from read.Db, or that isn't a query Sluice analysed, or whose reads it couldn't work out; otherwise
    // its reads are recorded. Then any read inside a transaction, where a compute would cache rows other callers
    // can't see yet, or an old snapshot under RepeatableRead or Serializable. The transaction is read as the write
    // side reads it, from EF's own inputs: the command's own transaction would miss an enlisted or ambient one.
    private static InvalidOperationException? Read(
        SluiceReads reads,
        DbCommand command,
        CommandEventData eventData
    )
    {
        if (IsWrite(eventData.CommandSource))
        {
            return new(
                $"An EF write ran inside a compute ({Statement(command)}). Writes can't run inside a compute, "
                    + "which is a read: run them before or after GetOrSetAsync."
            );
        }

        var context = eventData.Context;
        if (!ActiveCompute.InFetch(reads))
        {
            if (context is null || !ActiveCompute.Holds(reads, context))
            {
                return ForeignContext($"an EF command ({Statement(command)})");
            }

            if (TagOf(command, UntrackableTag) is { } reason)
            {
                return new(
                    $"Sluice can't track an EF query inside a compute: {reason}. Run it in the fetch of "
                        + "read.From(dependency, fetch), with a dependency on a Source you declare, and invalidate "
                        + "that source when its data changes."
                );
            }

            if (
                TagOf(command, ReadsTag) is not { } read
                || Dependencies(context, read) is not { } dependencies
            )
            {
                return new(
                    $"An EF command that isn't a LINQ query ran inside a compute ({Statement(command)}), so Sluice "
                        + "can't tell what it read. Run it in the fetch of read.From(dependency, fetch), with a "
                        + "dependency on a Source you declare."
                );
            }

            reads.From(dependencies);
            if (
                dependencies.Length > 0
                && _loggedWhole.TryAdd((ActiveCompute.QueryName(reads), read), true)
            )
            {
                LogWholeTypes(
                    context.GetService<ILoggerFactory>().CreateLogger<TrackedReads>(),
                    ActiveCompute.QueryName(reads),
                    string.Join(
                        ", ",
                        read.Split(Separator)
                            .Where(name => !name.EndsWith(HelperSuffix, StringComparison.Ordinal))
                            .Select(name => context.Model.FindEntityType(name)!.ShortName())
                    )
                );
            }
        }

        if (context is not null && !ContextTransaction.Of(context).IsNone)
        {
            return new(
                "Sluice's EF reads can't run inside a transaction: the compute would cache rows other callers can't "
                    + "see yet, or an old snapshot. Compute the value outside the transaction, or after it commits."
            );
        }

        return null;
    }

    // So that reading whole types is a choice, not a surprise: a busy type evicts such a query on every write.
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Sluice's cached query {Query} reads every row of {EntityTypes}, so a write to any of their rows evicts it. If that evicts it too often, read them through read.Entity or read.Children, so it depends only on the rows it reads."
    )]
    private static partial void LogWholeTypes(ILogger logger, string query, string entityTypes);

    private static InvalidOperationException ForeignContext(string what) =>
        new(
            $"{char.ToUpperInvariant(what[0])}{what[1..]} ran inside a compute on a context the compute didn't get "
                + "from read.Db<TContext>(). Another context may hold tracked entities, unsaved edits or a "
                + "transaction, and the cached value is served to every caller. Query the context read.Db<TContext>() "
                + "returns, or run the query in the fetch of read.From(dependency, fetch) with a Source you declare."
        );

    // The whole-type dependencies a reads tag names, built once per model and tag. Null when a name isn't in the
    // model, which only a tag copied by hand can do.
    private static Dependency[]? Dependencies(DbContext context, string tag)
    {
        var model = context.Model;
        var cache = model.GetOrAddRuntimeAnnotationValue(
            DependenciesAnnotation,
            static _ => new ConcurrentDictionary<string, Dependency[]?>(StringComparer.Ordinal),
            (object?)null
        );
        return cache.GetOrAdd(
            tag,
            static (tag, model) =>
            {
                var dependencies = new List<Dependency>();
                foreach (var name in tag.Split(Separator))
                {
                    if (name.EndsWith(HelperSuffix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (model.FindEntityType(name) is not { } entityType)
                    {
                        return null;
                    }

                    dependencies.Add(ModelSources.Entity(entityType).All);
                }

                return [.. dependencies];
            },
            model
        );
    }

    // EF 10 reports ExecuteDelete's command as ExecuteUpdate; ExecuteDelete is listed for when it reports its own.
    private static bool IsWrite(CommandSource source) =>
        source
            is CommandSource.SaveChanges
                or CommandSource.ExecuteSqlRaw
                or CommandSource.ExecuteUpdate
                or CommandSource.ExecuteDelete;

    // The text of the command's tag that starts with prefix, after it. Tags are the comment lines at the head of the
    // SQL, one "-- " line each.
    private static string? TagOf(DbCommand command, string prefix)
    {
        foreach (var line in command.CommandText.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("-- "))
            {
                return null;
            }

            if (line[3..].StartsWith(prefix))
            {
                return line[(3 + prefix.Length)..].ToString();
            }
        }

        return null;
    }

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

    /// <summary>Puts the analysis's tags on the query's first root, where EF collects them like any <c>TagWith</c>.</summary>
    private sealed class Tagging(IReadOnlyList<string> tags) : ExpressionVisitor
    {
        private static readonly MethodInfo _tagWith =
            typeof(EntityFrameworkQueryableExtensions).GetMethod(
                nameof(EntityFrameworkQueryableExtensions.TagWith)
            )!;

        private bool _done;

        internal static Expression Tag(Expression query, IReadOnlyList<string> tags) =>
            tags.Count == 0 ? query : new Tagging(tags).Visit(query)!;

        public override Expression? Visit(Expression? node)
        {
            if (_done || node is not QueryRootExpression root)
            {
                return _done ? node : base.Visit(node);
            }

            _done = true;
            Expression tagged = root;
            foreach (var tag in tags)
            {
                tagged = Expression.Call(
                    _tagWith.MakeGenericMethod(root.ElementType),
                    tagged,
                    Expression.Constant(tag)
                );
            }

            return tagged;
        }
    }

    /// <summary>
    /// Works out the entity types a query reads, in the tree and in what EF adds to it later: the query filters of
    /// every type it reaches, and their auto-included navigations. A navigation is matched by name on every entity
    /// type the instance can be (a derived type reached through a cast, an interface in generic code, a property
    /// bag's indexer), so it can only find too many. Anything it can't follow makes the query untrackable.
    /// </summary>
    private sealed class Analysis(IModel model) : ExpressionVisitor
    {
        private readonly List<IEntityType> _reached = [];

        // The root type of each read helper's query in the tree, which the helper's own dependency covers.
        private readonly List<IEntityType> _helpers = [];
        private bool _ignoresFilters;
        private List<string>? _ignoredFilters;
        private bool _ignoresAutoIncludes;

        // The type whose query filter is being visited, if any.
        private IEntityType? _filter;
        private string? _untrackable;

        /// <summary>The tag for the query: what it reads, or why that can't be known.</summary>
        internal static IReadOnlyList<string> Of(IModel model, Expression query)
        {
            var analysis = new Analysis(model);
            analysis.Visit(query);
            analysis.Expand();
            analysis.CheckResult(query.Type);
            return analysis.Tags();
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
                Untrackable(
                    $"{_filter.DisplayName()}'s query filter reads the context's state (such as a tenant or the "
                        + "current user), and the cached value is served to every caller whatever that state is. Call "
                        + "IgnoreQueryFilters(), filter by that value yourself, and put it in the cache key"
                );
            }

            return base.Visit(node);
        }

        protected override Expression VisitExtension(Expression node)
        {
            // Collections of plain values (an inline array, a list parameter) aren't roots yet: EF turns them into
            // roots after this interceptor runs. FromSql and table-valued functions are entity roots too, but their
            // SQL can read any table.
            if (node is QueryRootExpression root)
            {
                if (root.GetType() == typeof(EntityQueryRootExpression))
                {
                    Reach(((EntityQueryRootExpression)root).EntityType);
                }
                else
                {
                    Untrackable(
                        root switch
                        {
                            EntityQueryRootExpression entity
                                when root.GetType()
                                    .Name.StartsWith("FromSql", StringComparison.Ordinal) =>
                                $"it reads {entity.EntityType.ShortName()} through FromSql, whose SQL can read any table",
                            EntityQueryRootExpression entity =>
                                $"it reads {entity.EntityType.ShortName()} through a {root.GetType().Name}, which can read any table",
                            _ =>
                                $"it reads SqlQuery<{root.ElementType.Name}>, whose SQL can read any table",
                        }
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
                ReachNavigation(instance.Type, name);
            }
            else if (model.FindDbFunction(method) is { IsBuiltIn: false } function)
            {
                Untrackable(
                    $"it calls the database function {function.Name}, which can read any table"
                );
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is not null && node.Member.DeclaringType is { } declaringType)
            {
                ReachNavigation(declaringType, node.Member.Name);
            }

            return base.VisitMember(node);
        }

        private void VisitQueryOperator(MethodCallExpression node)
        {
            switch (node.Method.Name)
            {
                case nameof(EntityFrameworkQueryableExtensions.TagWith)
                    when node.Arguments[1] is ConstantExpression { Value: Marker }:
                    // The helpers mark the set itself, so the marker's source is the helper's root.
                    if (node.Arguments[0] is EntityQueryRootExpression helper)
                    {
                        _helpers.Add(helper.EntityType);
                    }

                    break;
                // A navigation path as text: each name is matched on every type, as a member is.
                case nameof(EntityFrameworkQueryableExtensions.Include)
                    when node.Arguments[1] is ConstantExpression { Value: string path }:
                    foreach (var name in path.Split('.'))
                    {
                        ReachNavigation(typeof(object), name);
                    }

                    break;
                case nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes):
                    _ignoresAutoIncludes = true;
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

        // Every non-ownership navigation with this name, on every entity type the instance can be. An owned value is
        // stored in its owner's row, so reading it reads nothing more.
        private void ReachNavigation(Type declaringType, string name)
        {
            if (declaringType.IsValueType)
            {
                return;
            }

            foreach (var entityType in model.GetEntityTypes())
            {
                if (!declaringType.IsAssignableFrom(entityType.ClrType))
                {
                    continue;
                }

                switch (
                    (INavigationBase?)entityType.FindNavigation(name)
                    ?? entityType.FindSkipNavigation(name)
                )
                {
                    case INavigation { ForeignKey.IsOwnership: false } navigation:
                        Reach(navigation.TargetEntityType);
                        break;
                    case ISkipNavigation skip:
                        Reach(skip.JoinEntityType);
                        Reach(skip.TargetEntityType);
                        break;
                }
            }
        }

        private void Reach(IEntityType entityType) => _reached.Add(entityType);

        // EF adds each reached type's query filters, unless the query ignores them, and its auto-included
        // navigations, unless the query ignores those; both can reach more types. Only a root type declares
        // filters. Each type is expanded once.
        private void Expand()
        {
            var expanded = new HashSet<IEntityType>();
            for (var i = 0; i < _reached.Count; i++)
            {
                var entityType = _reached[i];
                if (!expanded.Add(entityType))
                {
                    continue;
                }

                if (!_ignoresFilters)
                {
                    _filter = entityType.GetRootType();
                    foreach (var filter in _filter.GetDeclaredQueryFilters())
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

                if (!_ignoresAutoIncludes)
                {
                    foreach (
                        var navigation in entityType
                            .GetDerivedTypesInclusive()
                            .SelectMany(type => type.GetDeclaredNavigations())
                            .Concat(entityType.GetNavigations())
                    )
                    {
                        if (navigation is { IsEagerLoaded: true, ForeignKey.IsOwnership: false })
                        {
                            Reach(navigation.TargetEntityType);
                        }
                    }
                }
            }
        }

        // A cached entity must stand alone: one that loads lazily would read related rows later, through its context,
        // outside any compute, and one that holds its context keeps that context, and all it tracks, in the cache.
        // Owned values are materialised with their owner, so an owner is refused for what it owns too. The result is
        // checked by type: the element of a sequence, a single result, and what those hold through generic arguments
        // (anonymous types and tuples included) and arrays. An entity inside a class of your own isn't seen; an
        // object-typed value is taken for one.
        private void CheckResult(Type result)
        {
            List<(Type Type, string Reason)> refused = [];
            foreach (
                var entityType in _reached
                    .Distinct()
                    .SelectMany(type => type.GetDerivedTypesInclusive())
                    .SelectMany(WithOwned)
                    .Distinct()
            )
            {
                foreach (var part in WithOwned(entityType))
                {
                    if (Holds(part) is { } holds)
                    {
                        refused.Add(
                            (
                                entityType.ClrType,
                                $"it returns {entityType.ShortName()} entities (including what they own), and "
                                    + $"{part.ShortName()} entities {holds}. Select the values you need instead"
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

        private void CheckResult(Type type, List<(Type Type, string Reason)> refused)
        {
            if (_untrackable is not null)
            {
                return;
            }

            if (
                refused.Find(entity => type.IsAssignableFrom(entity.Type)) is { Reason: { } reason }
            )
            {
                Untrackable(reason);
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

        private void Untrackable(string reason) => _untrackable ??= reason;

        // What's read is named by root type, the source EF writes publish, with an owned type standing for the owner
        // whose row it's stored in. A helper's root is covered by the helper's own dependency unless the query reaches
        // that type again. A type whose rows aren't all in tables is never invalidated by a tracked write.
        private List<string> Tags()
        {
            var types = _reached.Select(ModelSources.Unowned).ToList();
            List<string> tags = [];
            if (_untrackable is null)
            {
                foreach (var entityType in types.Distinct())
                {
                    if (ModelSources.Unmapped(entityType) is { } reason)
                    {
                        Untrackable(
                            $"it reads {entityType.ShortName()}, which {reason}, so no tracked write invalidates it"
                        );
                        break;
                    }
                }
            }

            if (_untrackable is not null)
            {
                tags.Add(UntrackableTag + _untrackable);
            }
            else if (types.Count > 0)
            {
                // A type every reach of which is a helper's root is covered by those helpers.
                var helpers = _helpers.Select(helper => helper.GetRootType()).ToList();
                tags.Add(
                    ReadsTag
                        + string.Join(
                            Separator,
                            types
                                .GroupBy(type => type.GetRootType())
                                .Select(group =>
                                    group.Count() == helpers.Count(helper => helper == group.Key)
                                        ? group.Key.Name + HelperSuffix
                                        : group.Key.Name
                                )
                        )
                );
            }

            return tags;
        }
    }
}
