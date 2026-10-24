using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string TablePatternAnnotation = "Sluice:TablePattern";
    private const string RowVersionAnnotation = "Sluice:RowVersion";
    private const string UniqueColumnsAnnotation = "Sluice:UniqueColumns";

    /// <summary>
    /// The source of <paramref name="entityType"/>'s rows, keyed by primary key; a composite key is passed as an
    /// array of its values. Unchecked: a write to a type no read accepts has no reader to evict, and a throw would lose
    /// the rest of the save's invalidations. Public entry points check the mapping first (<see cref="EnsureTable"/>).
    /// </summary>
    internal static Source<object> Entity(IEntityType entityType)
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
    /// The source of the rows of <paramref name="property"/>'s type by that property's value, which
    /// <c>read.Entity</c> by a unique column reads. Only for a <see cref="UniqueColumns"/> property: a save publishes
    /// for no other.
    /// </summary>
    internal static Source<object> Unique(IProperty property) =>
        property.GetOrAddRuntimeAnnotationValue(
            SourceAnnotation,
            static property =>
                Build($"{property!.DeclaringType.Name}({property.Name})", [property]),
            property
        );

    /// <summary>
    /// The properties of <paramref name="entityType"/> and its base types that a single-column unique index (filtered
    /// or not) or alternate key covers, other than a single-column primary key and any whose value converter stores
    /// null as a value, whose rows <c>read.Entity</c> by a unique column can read. The model's unique columns bound
    /// what a save publishes; uniqueness itself isn't needed for correctness. Built once per type.
    /// </summary>
    internal static IProperty[] UniqueColumns(IEntityType entityType) =>
        entityType.GetOrAddRuntimeAnnotationValue(
            UniqueColumnsAnnotation,
            static entityType => entityType!.GetProperties().Where(IsUniqueColumn).ToArray(),
            entityType
        );

    /// <summary>The unique columns only <paramref name="entityType"/>'s derived types have.</summary>
    internal static IEnumerable<IProperty> DerivedUniqueColumns(IEntityType entityType) =>
        RowUniqueColumns(entityType).Except(UniqueColumns(entityType));

    // Every unique column a row of the type may have: its own and its base types', and its derived types'.
    private static IEnumerable<IProperty> RowUniqueColumns(IEntityType entityType) =>
        entityType.GetDerivedTypesInclusive().SelectMany(UniqueColumns).Distinct();

    // A single-column primary key is the type's own source, which every save publishes already. A column whose
    // converter stores null as a value is left out: a row whose value is null holds that value in the database, which
    // a reader by it finds, but no save would publish it.
    private static bool IsUniqueColumn(IProperty property) =>
        IsSingleColumnUnique(property) && !ConvertsNulls(property);

    private static bool IsSingleColumnUnique(IProperty property) =>
        property.FindContainingPrimaryKey() is not { Properties: [_] }
        && (
            property.GetContainingIndexes().Any(index => index.IsUnique && index.Properties is [_])
            || property.GetContainingKeys().Any(key => key.Properties is [_])
        );

    private static bool ConvertsNulls(IProperty property) =>
        property.GetTypeMapping().Converter is { ConvertsNulls: true };

    // A row whose value is null holds the converted value in the database, which a read by that value finds, but a
    // save reports the null, so it evicts no such read.
    private static NotSupportedException NullStoredAsValue(
        IEntityType entityType,
        IProperty property,
        string reading,
        string them
    ) =>
        new(
            $"{entityType.DisplayName()}.{property.Name}'s value converter stores null as a value, so a row whose "
                + $"{property.Name} is null holds a value that a read by that value finds, but no save evicts that "
                + $"read. {reading} isn't supported; query {them} from read.Db<TContext>(), which depends on every "
                + $"{entityType.DisplayName()} row."
        );

    /// <summary>
    /// Resolves <paramref name="selector"/> (a property, or <c>EF.Property</c> on a shadow property) to a column of
    /// <paramref name="entityType"/> that <see cref="UniqueColumns"/> lists, or to its single-column primary key.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The selector doesn't name a property, names it through <c>EF.Property</c> with a name that isn't a constant, or
    /// names one no single-column unique index or alternate key covers.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Only a composite unique index or alternate key covers it, or its value converter stores null as a value.
    /// </exception>
    internal static IProperty ResolveUniqueColumn(
        IEntityType entityType,
        LambdaExpression selector,
        string parameterName
    )
    {
        var property = SelectedName(
            selector,
            parameterName,
            "f => EF.Property<string>(f, \"Slug\")"
        )
            is { } name
            ? entityType.FindProperty(name)
            : null;
        if (property is null)
        {
            throw new ArgumentException(
                $"'{selector}' doesn't name a property of {entityType.DisplayName()}. Pass the property (f => f.Slug) "
                    + "or a shadow property (f => EF.Property<string>(f, \"Slug\")).",
                parameterName
            );
        }

        if (property.IsPrimaryKey() && entityType.FindPrimaryKey()!.Properties is [_])
        {
            return property;
        }

        if (UniqueColumns(entityType).Contains(property))
        {
            return property;
        }

        var column = $"{entityType.DisplayName()}.{property.Name}";
        if (IsSingleColumnUnique(property) && ConvertsNulls(property))
        {
            throw NullStoredAsValue(entityType, property, "Reading one row by such a column", "it");
        }

        if (
            property.GetContainingIndexes().Any(index => index.IsUnique)
            || property.GetContainingKeys().Any()
        )
        {
            throw new NotSupportedException(
                $"{column} is unique only together with other columns (a composite unique index or key). Reading one "
                    + "row by a composite unique index isn't supported; query it from read.Db<TContext>(), which "
                    + $"depends on every {entityType.DisplayName()} row."
            );
        }

        throw new ArgumentException(
            $"{column} isn't covered by a single-column unique index or alternate key in the EF model, and a save "
                + "invalidates readers by value only for those columns. Map one (HasIndex(f => f.Slug).IsUnique(); a "
                + "filtered index counts), or query it from read.Db<TContext>(), which depends on every "
                + $"{entityType.DisplayName()} row.",
            parameterName
        );
    }

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
    /// Whether EF's <c>UPDATE</c> or <c>DELETE</c> of a row of <paramref name="entityType"/> fails unless the row is
    /// unchanged since it was loaded: the row is stored in one table, which has a concurrency token the database changes
    /// on every write (<c>rowversion</c>, PostgreSQL's <c>xmin</c>), so every column EF loaded is what it still holds. A
    /// token only the app changes doesn't count: a bulk write, raw SQL or another service may not change it. Nor does
    /// a row split across tables (table-per-type, entity splitting), whose token is in one of them. Built once per type.
    /// </summary>
    internal static bool HasRowVersion(IEntityType entityType) =>
        entityType.GetOrAddRuntimeAnnotationValue(
            RowVersionAnnotation,
            static entityType =>
                entityType!
                    .GetProperties()
                    .Any(property =>
                        property.IsConcurrencyToken
                        && property.ValueGenerated
                            is ValueGenerated.OnAddOrUpdate
                                or ValueGenerated.OnUpdate
                    ) && SingleTable(entityType!),
            entityType
        );

    /// <summary>
    /// Whether a row of <paramref name="entityType"/> is stored in one table, so EF's <c>UPDATE</c> of it checks its
    /// concurrency tokens whichever columns it writes. A row split across tables (table-per-type, entity splitting)
    /// is written one table at a time, and a token in one table isn't checked by a write to another.
    /// </summary>
    internal static bool SingleTable(IEntityType entityType) =>
        entityType.GetTableMappings().Count() == 1;

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
                && entityType
                    .GetOrAddRuntimeAnnotationValue(
                        TablePatternAnnotation,
                        static arg => NamePattern(arg.Quote.DelimitIdentifier(arg.Table)),
                        (Quote: quote, Table: table)
                    )
                    .IsMatch(sql)
            )
            .Select(Unowned)
            .Select(entityType => entityType.GetRootType())
            .Distinct();
    }

    /// <summary>
    /// What an update of <paramref name="entityType"/> rows may change: the whole type, every foreign key a row of it
    /// may have, since an update may move rows between parents, and every unique column a row of it may have, since
    /// it may change their values. Unchecked, like <see cref="Entity"/>.
    /// </summary>
    internal static IEnumerable<Dependency> UpdateDependencies(IEntityType entityType) =>
        [
            Entity(entityType).All,
            .. RowForeignKeys(entityType).Select(foreignKey => ForeignKey(foreignKey).All),
            .. RowUniqueColumns(entityType).Select(property => Unique(property).All),
        ];

    /// <summary>
    /// Every dependency on <paramref name="entityType"/>: its <see cref="UpdateDependencies"/>, and every foreign key
    /// that points at it and lets the database act on its rows, so every read of it is evicted. Also the same for
    /// every type the model lets the database delete or set to null when its rows are deleted. Unchecked, like
    /// <see cref="Entity"/>.
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
        EnsureTable(entityType);
        EnsureKeyType(KeyProperty(entityType), key, parameterName);
        return Entity(entityType).For(key);
    }

    /// <summary>The single-column primary key of <paramref name="entityType"/>, which <see cref="EnsureTable"/> has checked.</summary>
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
    /// <exception cref="NotSupportedException">The foreign key's value converter stores null as a value.</exception>
    internal static IForeignKey ResolveForeignKey(
        IEntityType entityType,
        LambdaExpression selector,
        string parameterName
    )
    {
        var name = SelectedName(
            selector,
            parameterName,
            "o => EF.Property<CustomerId>(o, \"CustomerId\")"
        );
        var foreignKey =
            name is null ? null
            : entityType.FindNavigation(name)
                is { IsCollection: false, IsOnDependent: true } navigation
                ? navigation.ForeignKey
            : entityType
                .FindProperty(name)
                ?.GetContainingForeignKeys()
                .FirstOrDefault(foreignKey => foreignKey.Properties is [_]);

        if (foreignKey is not { Properties: [var property] })
        {
            throw new ArgumentException(
                $"'{selector}' doesn't name a single-column foreign key of {entityType.DisplayName()}. Pass the FK "
                    + "property (o => o.CustomerId), a shadow FK (o => EF.Property<CustomerId>(o, \"CustomerId\")) or a "
                    + "reference navigation to the principal (o => o.Customer).",
                parameterName
            );
        }

        return ConvertsNulls(property)
            ? throw NullStoredAsValue(
                entityType,
                property,
                "Reading children by such a foreign key",
                "them"
            )
            : foreignKey;
    }

    // The name of the member a selector reads off its parameter, directly or through EF.Property with a constant
    // name, unwrapping the conversion to object; null for anything else.
    private static string? SelectedName(
        LambdaExpression selector,
        string parameterName,
        string example
    )
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : selector.Body;
        return body switch
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
                        + $"must be a constant string ({example}), so the property can be found from the expression "
                        + "without running it.",
                    parameterName
                ),
            _ => null,
        };
    }

    /// <summary>Refuses a value its column's converter stores as null, which a read by value never finds.</summary>
    /// <exception cref="ArgumentException">The converter stores <paramref name="value"/> as null.</exception>
    internal static void EnsureStored(IProperty property, object value, string parameterName)
    {
        if (StoredValue(property, value) is null)
        {
            throw new ArgumentException(
                $"The value converter of {property.DeclaringType.DisplayName()}.{property.Name} stores '{value}' as "
                    + "null, which a read by value never finds. Read the rows whose column is null with a query on "
                    + "read.Db<TContext>().",
                parameterName
            );
        }
    }

    /// <summary>Checks that <paramref name="key"/> has the CLR type of <paramref name="property"/>, ignoring nullability.</summary>
    /// <exception cref="ArgumentException">It doesn't.</exception>
    internal static void EnsureKeyType(
        IProperty property,
        object key,
        string parameterName,
        string what = "key"
    )
    {
        var expected = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (key.GetType() != expected)
        {
            throw new ArgumentException(
                $"The {what} for {property.DeclaringType.DisplayName()}.{property.Name} must be of type {expected.Name}, "
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

                dependencies.Add(Entity(dependent).All);
                dependencies.AddRange(
                    RowUniqueColumns(dependent).Select(property => Unique(property).All)
                );
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
    /// The pattern of <paramref name="identifier"/>, as the provider delimits it, standing alone in SQL: not part of a
    /// longer identifier, so an unquoted <c>users</c> (Npgsql leaves a lowercase name bare) isn't found in
    /// <c>old_users</c> or <c>users_id</c>. A quoted one carries its own delimiters, so its ends need no check.
    /// </summary>
    internal static Regex NamePattern(string identifier) =>
        new(
            (IsWordChar(identifier[0]) ? @"(?<![\w$])" : "")
                + Regex.Escape(identifier)
                + (IsWordChar(identifier[^1]) ? @"(?![\w$])" : ""),
            RegexOptions.CultureInvariant
        );

    private static bool IsWordChar(char c) => c == '$' || c == '_' || char.IsLetterOrDigit(c);

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

    // A column's value as a read by value reads it: null where the column holds null, as it does where the value
    // converter stores the value as null, since no read by value finds a null.
    internal static object? StoredValue(IProperty property, object? value) =>
        value is not null
        && (
            property.GetTypeMapping().Converter is not { } converter
            || converter.ConvertToProvider(value) is not null
        )
            ? value
            : null;

    // The value the database compares, so a strongly-typed id reads and writes as the column's value.
    private static object ToProvider(IProperty property, object key) =>
        property.GetTypeMapping().Converter is { } converter
            ? converter.ConvertToProvider(key)
                ?? throw new InvalidOperationException(
                    $"The value converter of {property.DeclaringType.DisplayName()}.{property.Name} returned null for '{key}'."
                )
            : key;
}
