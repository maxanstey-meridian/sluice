using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The core sources an EF model implies: one per root entity type, named by EF so TPH and TPT reads and writes
/// agree, and one per foreign key. Each is built once per model, through the core's public <c>format</c>
/// overload, and kept as a runtime annotation on the model.
/// </summary>
internal static class ModelSources
{
    private const string SourceAnnotation = "Sluice:Source";
    private const string CascadeAnnotation = "Sluice:Cascades";

    /// <summary>The source of <paramref name="entityType"/>'s rows, keyed by primary key.</summary>
    /// <exception cref="NotSupportedException">The type isn't mapped to a table.</exception>
    internal static Source<object> Entity(IEntityType entityType)
    {
        EnsureTable(entityType);
        return WrittenEntity(entityType);
    }

    /// <summary>
    /// The same source without the mapping check, for writes: a type no read accepts has no reader to evict, and a
    /// throw would lose the rest of the save's invalidations. A composite key is passed as an array of its values.
    /// </summary>
    internal static Source<object> WrittenEntity(IEntityType entityType)
    {
        var root = entityType.GetRootType();
        return root.GetOrAddRuntimeAnnotationValue(
            SourceAnnotation,
            static root => Build(root!.Name, root.FindPrimaryKey()!.Properties),
            root
        );
    }

    /// <summary>The source of the rows that point at one principal through <paramref name="foreignKey"/>.</summary>
    internal static Source<object> ForeignKey(IForeignKey foreignKey) =>
        foreignKey.GetOrAddRuntimeAnnotationValue(
            SourceAnnotation,
            static foreignKey =>
                Build(
                    $"{foreignKey!.DeclaringEntityType.GetRootType().Name}[{string.Join(",", foreignKey.Properties.Select(property => property.Name))}]",
                    foreignKey.Properties
                ),
            foreignKey
        );

    /// <summary>
    /// The foreign keys pointing at <paramref name="entityType"/>, its base types or its derived types whose
    /// delete behaviour lets the database act on their rows when a principal is deleted: <c>Cascade</c> deletes them,
    /// <c>SetNull</c> sets their key to null. Ownerships are left out: an owned row goes with its owner's key. So are
    /// the keys table-per-type mapping links a derived type's table to its base type's with: that's the same row.
    /// </summary>
    internal static IEnumerable<IForeignKey> CascadingForeignKeys(IEntityType entityType) =>
        entityType
            .GetReferencingForeignKeys()
            .Concat(
                entityType
                    .GetDerivedTypes()
                    .SelectMany(derived => derived.GetDeclaredReferencingForeignKeys())
            )
            .Where(foreignKey =>
                !foreignKey.IsOwnership
                && !foreignKey.IsBaseLinking()
                && foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull
            );

    /// <summary>
    /// What the database changes when rows of <paramref name="entityType"/> are deleted, as the model's delete
    /// behaviours say, beyond the <see cref="CascadingForeignKeys"/> pointing at those rows: the whole type of every
    /// row it deletes or sets to null, transitively through cascades; every other foreign key of a row it deletes;
    /// and every cascading foreign key past the first, whose principals' keys nobody knows. Built once per type.
    /// </summary>
    internal static Dependency[] Cascades(IEntityType entityType) =>
        entityType.GetOrAddRuntimeAnnotationValue(
            CascadeAnnotation,
            static entityType => BuildCascades(entityType!),
            entityType
        );

    /// <summary>
    /// The entity types whose tables <paramref name="sql"/> names, as the provider quotes them, mapped to the types
    /// that store rows there. It can only find too many: a column or alias with a table's name matches it too.
    /// </summary>
    /// <remarks>
    /// For a bulk write, whose SQL names the table it writes. EF drops query tags from <c>ExecuteUpdate</c> and
    /// <c>ExecuteDelete</c> (EF Core 10), so this is the only per-execution record of what one wrote.
    /// </remarks>
    internal static IEnumerable<IEntityType> Named(DbContext context, string sql)
    {
        var quote = context.GetService<ISqlGenerationHelper>();
        return context
            .Model.GetEntityTypes()
            .Where(entityType =>
                entityType.FindPrimaryKey() is not null
                && entityType.GetTableName() is { } table
                && Names(sql, quote.DelimitIdentifier(table))
            )
            .Select(Unowned)
            .Select(entityType => entityType.GetRootType())
            .Distinct();
    }

    /// <summary>
    /// What an update of <paramref name="entityType"/> rows may change: the whole type, and every foreign key a row of
    /// it may have, since an update may move rows between parents. Unchecked, like <see cref="WrittenEntity"/>.
    /// </summary>
    internal static IEnumerable<Dependency> UpdateDependencies(IEntityType entityType) =>
        [
            WrittenEntity(entityType).All,
            .. RowForeignKeys(entityType).Select(foreignKey => ForeignKey(foreignKey).All),
        ];

    /// <summary>
    /// Every dependency on <paramref name="entityType"/>: its <see cref="UpdateDependencies"/>, and every foreign key
    /// that points at it and lets the database act on its rows, so every read of it is evicted. Also the same for
    /// every type the model lets the database delete or set to null when its rows are deleted. Unchecked, like
    /// <see cref="WrittenEntity"/>.
    /// </summary>
    internal static IEnumerable<Dependency> AllDependencies(IEntityType entityType) =>
        [
            .. UpdateDependencies(entityType),
            .. CascadingForeignKeys(entityType).Select(foreignKey => ForeignKey(foreignKey).All),
            .. Cascades(entityType),
        ];

    /// <summary>
    /// The dependency on the <paramref name="entityType"/> row whose primary key is <paramref name="key"/>, which
    /// <c>read.Entity</c> records and <c>set.Dependency</c> returns.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> isn't of the primary key's CLR type.</exception>
    /// <exception cref="NotSupportedException">The type isn't mapped to a table, or its primary key is composite.</exception>
    internal static Dependency EntityDependency(
        IEntityType entityType,
        object key,
        string parameterName
    )
    {
        var source = Entity(entityType);
        EnsureKeyType(KeyProperty(entityType), key, parameterName);
        return source.For(key);
    }

    /// <summary>The single-column primary key of <paramref name="entityType"/>, which <see cref="Entity"/> has checked.</summary>
    /// <exception cref="NotSupportedException">The primary key is composite.</exception>
    internal static IProperty KeyProperty(IEntityType entityType) =>
        entityType.FindPrimaryKey()!.Properties is [var property]
            ? property
            : throw new NotSupportedException(
                $"{entityType.DisplayName()} has a composite primary key. Reading or invalidating one row by "
                    + "composite key isn't supported; query it from read.Db<TContext>(), or invalidate it with "
                    + "AllDependencies()."
            );

    /// <summary>
    /// Resolves <paramref name="selector"/> (an FK property, <c>EF.Property</c> on a shadow FK, or a reference
    /// navigation) to a single-column foreign key of <paramref name="entityType"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The selector doesn't name a single-column foreign key, or names it through <c>EF.Property</c> with a property
    /// name that isn't a constant.
    /// </exception>
    internal static IForeignKey ResolveForeignKey(
        IEntityType entityType,
        LambdaExpression selector,
        string parameterName
    )
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : selector.Body;
        var name = body switch
        {
            MemberExpression { Expression: ParameterExpression } member => member.Member.Name,
            MethodCallExpression
            {
                Method.Name: nameof(EF.Property),
                Arguments: [ParameterExpression, var property],
            } call when call.Method.DeclaringType == typeof(EF) => property
                is ConstantExpression { Value: string constant }
                ? constant
                : throw new ArgumentException(
                    $"'{selector}' names its property with '{property}'. The property name passed to EF.Property "
                        + "must be a constant string (o => EF.Property<CustomerId>(o, \"CustomerId\")), so the foreign key "
                        + "can be found from the expression without running it.",
                    parameterName
                ),
            _ => null,
        };

        var foreignKey =
            name is null ? null
            : entityType.FindNavigation(name)
                is { IsCollection: false, IsOnDependent: true } navigation
                ? navigation.ForeignKey
            : entityType
                .FindProperty(name)
                ?.GetContainingForeignKeys()
                .FirstOrDefault(foreignKey => foreignKey.Properties is [_]);

        return foreignKey is { Properties: [_] }
            ? foreignKey
            : throw new ArgumentException(
                $"'{selector}' doesn't name a single-column foreign key of {entityType.DisplayName()}. Pass the FK "
                    + "property (o => o.CustomerId), a shadow FK (o => EF.Property<CustomerId>(o, \"CustomerId\")) or a "
                    + "reference navigation to the principal (o => o.Customer).",
                parameterName
            );
    }

    /// <summary>Checks that <paramref name="key"/> has the CLR type of <paramref name="property"/>, ignoring nullability.</summary>
    /// <exception cref="ArgumentException">It doesn't.</exception>
    internal static void EnsureKeyType(IProperty property, object key, string parameterName)
    {
        var expected = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (key.GetType() != expected)
        {
            throw new ArgumentException(
                $"The key for {property.DeclaringType.DisplayName()}.{property.Name} must be of type {expected.Name}, "
                    + $"its type in the EF model, but was of type {key.GetType().Name}.",
                parameterName
            );
        }
    }

    /// <summary>
    /// Refuses types no tracked write ever publishes for, since a cached read of one would never be invalidated.
    /// </summary>
    /// <exception cref="NotSupportedException">The type isn't mapped to a table.</exception>
    internal static void EnsureTable(IEntityType entityType)
    {
        if (Unmapped(entityType) is { } reason)
        {
            throw new NotSupportedException(
                $"{entityType.DisplayName()} {reason}, so no tracked write can invalidate a cached read of it. "
                    + "Query it in the fetch of read.From(dependency, fetch), with a dependency on a Source you "
                    + "declare, and invalidate that source after it changes."
            );
        }
    }

    /// <summary>
    /// Why the type's rows aren't all in tables, if they aren't: its own, or those of a concrete derived type, which a
    /// query of the type returns too. An abstract type has no rows of its own, so it needs no table of its own (the
    /// root of a TPC hierarchy).
    /// </summary>
    internal static string? Unmapped(IEntityType entityType) =>
        UnmappedRows(entityType)
        ?? entityType
            .GetDerivedTypes()
            .Where(derived => !derived.IsAbstract())
            .Select(derived =>
                UnmappedRows(derived) is { } reason
                    ? $"has the derived type {derived.DisplayName()}, which {reason}"
                    : null
            )
            .FirstOrDefault(reason => reason is not null);

    private static string? UnmappedRows(IEntityType entityType) =>
        entityType.FindPrimaryKey() is null ? "is keyless"
        : entityType.GetViewName() is { } view ? $"is mapped to the view '{view}'"
        : entityType.GetFunctionName() is { } function ? $"is mapped to the function '{function}'"
        : entityType.GetSqlQuery() is not null ? "is mapped to a SQL query"
        : entityType.GetTableName() is not null || entityType.IsAbstract() ? null
        : "isn't mapped to a table";

    // A walk of the model from the deleted type, through each cascade once, so a self-reference or a loop ends. A row
    // the database deletes leaves every parent it had and takes its own dependents with it; one it sets to null keeps
    // them. A dependent that's owned is stored in its owner's rows, and a keyless one has no reader.
    private static Dependency[] BuildCascades(IEntityType deleted)
    {
        var dependencies = new List<Dependency>();
        var reached = new HashSet<IEntityType>();
        var pending = new Queue<(IEntityType Type, bool First)>([(deleted, true)]);
        while (pending.TryDequeue(out var principal))
        {
            foreach (var foreignKey in CascadingForeignKeys(principal.Type))
            {
                var dependent = Unowned(foreignKey.DeclaringEntityType);
                if (dependent.FindPrimaryKey() is null)
                {
                    continue;
                }

                dependencies.Add(WrittenEntity(dependent).All);
                if (!principal.First)
                {
                    dependencies.Add(ForeignKey(foreignKey).All);
                }

                // A deleted or nulled row changes in every list it's in, whichever parent the list is under.
                dependencies.AddRange(
                    RowForeignKeys(dependent)
                        .Where(other => other != foreignKey)
                        .Select(other => ForeignKey(other).All)
                );
                if (foreignKey.DeleteBehavior is DeleteBehavior.Cascade && reached.Add(dependent))
                {
                    pending.Enqueue((dependent, false));
                }
            }
        }

        return [.. dependencies];
    }

    // Every foreign key a row of the type may have: its own and its base types', and its derived types'.
    private static IEnumerable<IForeignKey> RowForeignKeys(IEntityType entityType) =>
        entityType.GetForeignKeys().Concat(entityType.GetDerivedForeignKeys());

    /// <summary>
    /// Whether <paramref name="sql"/> names <paramref name="identifier"/>, as the provider delimits it. One left unquoted
    /// (Npgsql leaves a lowercase name bare) must stand alone, not be part of a longer identifier: <c>user</c> in
    /// <c>users</c> or <c>user_id</c> isn't it. A quoted one carries its own delimiters.
    /// </summary>
    internal static bool Names(string sql, string identifier)
    {
        if (!IsIdentifierChar(identifier[0]))
        {
            return sql.Contains(identifier, StringComparison.Ordinal);
        }

        for (
            var at = sql.IndexOf(identifier, StringComparison.Ordinal);
            at >= 0;
            at = sql.IndexOf(identifier, at + 1, StringComparison.Ordinal)
        )
        {
            var end = at + identifier.Length;
            if (
                (at == 0 || !IsIdentifierChar(sql[at - 1]))
                && (end == sql.Length || !IsIdentifierChar(sql[end]))
            )
            {
                return true;
            }
        }

        return false;
    }

    // A character an unquoted SQL identifier can hold.
    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The entity whose rows <paramref name="entityType"/> is stored with: itself, unless it's owned.</summary>
    internal static IEntityType Unowned(IEntityType entityType) =>
        entityType.FindOwnership() is { } ownership
            ? Unowned(ownership.PrincipalEntityType)
            : entityType;

    // A composite key, which only writes produce, arrives as an array of its values in key order.
    private static Source<object> Build(string name, IReadOnlyList<IProperty> properties) =>
        properties is [var property]
            ? new(name, key => KeyFolding.Fold(ToProvider(property, key)))
            : new(
                name,
                key =>
                    JsonSerializer.Serialize(
                        ((object[])key)
                            .Select((value, i) => KeyFolding.Fold(ToProvider(properties[i], value)))
                            .ToArray()
                    )
            );

    // The value the database compares, so a strongly-typed id reads and writes as the column's value.
    private static object ToProvider(IProperty property, object key) =>
        property.GetTypeMapping().Converter is { } converter
            ? converter.ConvertToProvider(key)
                ?? throw new InvalidOperationException(
                    $"The value converter of {property.DeclaringType.DisplayName()}.{property.Name} returned null for '{key}'."
                )
            : key;
}
