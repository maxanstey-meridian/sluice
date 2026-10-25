using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The core sources an EF model implies, named after the tables they read so every class mapped to a table, in any
/// context, meets on one source: one per root entity type, so TPH and TPT reads and writes agree, one per foreign key
/// and one per unique column. Each is built once per model, through
/// <see cref="Sluice.Infrastructure.ModelSource.Create{TKey}"/>, and kept as a runtime annotation on the model.
/// </summary>
internal static class ModelSources
{
    private const string SourceAnnotation = "Sluice:Source";
    private const string RowSharersAnnotation = "Sluice:RowSharers";
    private const string RoundTripsAnnotation = "Sluice:RoundTrips";
    private const string SourcesAnnotation = "Sluice:Sources";
    private const string CascadeAnnotation = "Sluice:Cascades";
    private const string TablePatternAnnotation = "Sluice:TablePattern";
    private const string RowVersionAnnotation = "Sluice:RowVersion";
    private const string UniqueColumnsAnnotation = "Sluice:UniqueColumns";
    private const string ColumnPatternsAnnotation = "Sluice:ColumnPatterns";

    /// <summary>
    /// The most keys one dependency list names row by row: a keyed bulk write's keys, and the rows a read by a unique
    /// column found. Past it, the whole type stands in, so a mass write doesn't publish thousands of tags.
    /// </summary>
    internal const int KeyLimit = 100;

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
            static root => Build(root!, SourceName(root!), root!.FindPrimaryKey()!.Properties),
            root
        );
    }

    /// <summary>
    /// The source of the rows that point at one principal through <paramref name="foreignKey"/>, named
    /// <c>{source}[{columns}]</c> after its type's <see cref="SourceName"/> and its columns.
    /// </summary>
    internal static Source<object> ForeignKey(IForeignKey foreignKey) =>
        foreignKey.GetOrAddRuntimeAnnotationValue(
            SourceAnnotation,
            static foreignKey =>
                Build(
                    foreignKey!.DeclaringEntityType,
                    $"{SourceName(foreignKey.DeclaringEntityType)}[{string.Join(",", foreignKey.Properties.Select(ColumnName))}]",
                    foreignKey.Properties
                ),
            foreignKey
        );

    /// <summary>
    /// The source of the rows of <paramref name="property"/>'s type by that property's value, which
    /// <c>read.Entity</c> by a unique column reads, named <c>{source}({column})</c>. Only for a
    /// <see cref="UniqueColumns"/> property: a save publishes for no other.
    /// </summary>
    internal static Source<object> Unique(IProperty property) =>
        property.GetOrAddRuntimeAnnotationValue(
            SourceAnnotation,
            static property =>
                Build(
                    property!.DeclaringType.ContainingEntityType,
                    $"{SourceName(property.DeclaringType.ContainingEntityType)}({ColumnName(property)})",
                    [property]
                ),
            property
        );

    /// <summary>
    /// What names <paramref name="entityType"/>'s sources: its root's table, as <c>schema.table</c> when the model gives
    /// a schema; a table-per-concrete-type root with no table of its own is named for its concrete types' tables, sorted
    /// and joined with <c>+</c>. An owned type is its owner's. A type stored in no table keeps its root's EF name: no
    /// read of one is accepted.
    /// </summary>
    internal static string SourceName(IEntityType entityType)
    {
        var root = Unowned(entityType).GetRootType();
        if (TableName(root) is { } table)
        {
            return table;
        }

        var tables = root.GetDerivedTypes()
            .Select(TableName)
            .OfType<string>()
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        return tables.Count > 0 ? string.Join("+", tables) : root.Name;
    }

    private static string? TableName(IEntityType entityType) =>
        entityType.GetTableName() is { } table
            ? entityType.GetSchema() is { } schema
                ? $"{schema}.{table}"
                : table
            : null;

    // The column a property is stored in, in its type's table; a type with no table of its own (a TPC root) names it
    // as its concrete types do by default.
    private static string ColumnName(IProperty property) =>
        StoreObjectIdentifier.Create(property.DeclaringType, StoreObjectType.Table) is { } table
        && property.GetColumnName(table) is { } column
            ? column
            : property.GetColumnName();

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
    /// every type the model lets the database delete or set to null when its rows are deleted, and, unless
    /// <paramref name="sharers"/> is false, for every type stored in the same rows (<see cref="RowSharers"/>), whose
    /// rows a delete deletes too. Unchecked, like <see cref="Entity"/>.
    /// </summary>
    internal static IEnumerable<Dependency> AllDependencies(
        IEntityType entityType,
        bool sharers = true
    ) =>
        [
            .. UpdateDependencies(entityType),
            .. CascadingForeignKeys(entityType).Select(foreignKey => ForeignKey(foreignKey).All),
            .. Cascades(entityType),
            .. sharers
                ? entityType
                    .GetDerivedTypesInclusive()
                    .SelectMany(RowSharers)
                    .Select(sharer => sharer.Root)
                    .Distinct()
                    .SelectMany(root => AllDependencies(root, sharers: false))
                : [],
        ];

    /// <summary>
    /// The other root entity types stored in a table <paramref name="entityType"/>'s rows are stored in (table
    /// splitting), each with how a save of one of its rows changes theirs. A type in the same hierarchy is the same
    /// source, and an owned type is its owner, so neither is one. Built once per type.
    /// </summary>
    internal static RowSharer[] RowSharers(IEntityType entityType) =>
        entityType.GetOrAddRuntimeAnnotationValue(
            RowSharersAnnotation,
            static entityType => BuildRowSharers(entityType!),
            entityType
        );

    private static RowSharer[] BuildRowSharers(IEntityType entityType)
    {
        var root = entityType.GetRootType();
        var sharers = new List<RowSharer>();
        foreach (var mapping in entityType.GetTableMappings().Where(m => m.Table.IsShared))
        {
            var table = mapping.Table;
            var own = mapping
                .ColumnMappings.Concat(ComplexColumns(entityType, table))
                .Concat(OwnedColumns(entityType, table))
                .ToList();
            IReadOnlyList<IProperty> Writing(IEnumerable<IColumn?> columns) =>
                [.. own.Where(c => columns.Contains(c.Column)).Select(c => c.Property)];

            foreach (
                var other in table
                    .EntityTypeMappings.GroupBy(m =>
                        Unowned(m.TypeBase.ContainingEntityType).GetRootType()
                    )
                    .Where(other => other.Key != root && other.Key.FindPrimaryKey() is not null)
            )
            {
                var key = other.Key.FindPrimaryKey()!.Properties;
                var keyColumns = key.Select(table.FindColumn).ToList();
                var ownKey = keyColumns
                    .Select(column => own.FirstOrDefault(c => c.Column == column)?.Property)
                    .ToList();
                var theirs = other
                    .SelectMany(m => m.ColumnMappings.Concat(ComplexColumns(m.TypeBase, table)))
                    .Select(c => c.Column)
                    .ToHashSet();
                var shared = Writing(theirs.Except(keyColumns.OfType<IColumn>()));

                // A foreign key on the key's columns only links the row to itself.
                var columns = RowForeignKeys(other.Key)
                    .Where(foreignKey =>
                        !foreignKey.Properties.All(property => property.IsPrimaryKey())
                    )
                    .Select(foreignKey =>
                        (
                            Whole: ForeignKey(foreignKey).All,
                            Writers: Writing(foreignKey.Properties.Select(table.FindColumn))
                        )
                    )
                    .Concat(
                        RowUniqueColumns(other.Key)
                            .Select(property =>
                                (
                                    Whole: Unique(property).All,
                                    Writers: Writing([table.FindColumn(property)])
                                )
                            )
                    )
                    .Where(column => column.Writers.Count > 0)
                    .ToList();
                sharers.Add(
                    new(
                        other.Key,
                        key,
                        ownKey.Contains(null) ? null : [.. ownKey.OfType<IProperty>()],
                        shared,
                        columns
                    )
                );
            }
        }

        return [.. sharers];
    }

    // The columns of the types a type owns stored in the table's own columns (in its row), at any depth, with their
    // complex properties': each owned type maps them apart from its owner, and a save writes them as its owner's row.
    private static IEnumerable<IColumnMapping> OwnedColumns(IEntityType entityType, ITable table) =>
        entityType
            .GetReferencingForeignKeys()
            .Where(foreignKey => foreignKey.IsOwnership)
            .Select(ownership => ownership.DeclaringEntityType)
            .SelectMany(owned =>
                owned
                    .GetTableMappings()
                    .Where(mapping => mapping.Table == table)
                    .SelectMany(mapping => mapping.ColumnMappings)
                    .Concat(ComplexColumns(owned, table))
                    .Concat(OwnedColumns(owned, table))
            );

    // The columns of a type's complex properties stored in the table's own columns, at any depth, which EF maps apart
    // from the type's other columns. A complex collection is stored as JSON, in a column of its own.
    private static IEnumerable<IColumnMapping> ComplexColumns(ITypeBase type, ITable table) =>
        type.GetComplexProperties()
            .Where(property => !property.IsCollection)
            .SelectMany(property =>
                property
                    .ComplexType.GetTableMappings()
                    .Where(mapping => mapping.Table == table)
                    .SelectMany(mapping => mapping.ColumnMappings)
                    .Concat(ComplexColumns(property.ComplexType, table))
            );

    /// <summary>
    /// Another root entity type whose rows share a table with a saved type's (<see cref="RowSharers"/>).
    /// </summary>
    /// <param name="Root">The other type.</param>
    /// <param name="Key">The other type's primary key.</param>
    /// <param name="OwnKey">
    /// The saved type's properties in those key columns, in that order; null when it maps none of one.
    /// </param>
    /// <param name="Shared">The saved type's properties in the other type's other columns.</param>
    /// <param name="Columns">
    /// The other type's foreign keys and unique columns that the saved type's properties write, whole, each with those
    /// properties.
    /// </param>
    internal sealed record RowSharer(
        IEntityType Root,
        IReadOnlyList<IProperty> Key,
        IReadOnlyList<IProperty>? OwnKey,
        IReadOnlyList<IProperty> Shared,
        IReadOnlyList<(Dependency Whole, IReadOnlyList<IProperty> Writers)> Columns
    );

    /// <summary>
    /// What an update of the <paramref name="entityType"/> rows with these primary keys may change, given its
    /// <paramref name="sql"/>: each row, which also reaches the type's whole-type readers; every foreign key a row may
    /// have, whole, since the rows may have moved between parents nobody knows; and every unique column a row may have
    /// whose column <paramref name="sql"/> names, as the provider delimits it, whole, since a reader by a value no row
    /// held recorded no key. A reader by a unique column that found one of the rows recorded its key
    /// (<c>read.Entity</c>). A statement that names the primary key's column more than once, beyond the key condition
    /// Sluice added, may set it, moving a row to a key whose readers found nothing, so it publishes
    /// <see cref="UpdateDependencies"/> instead. Unchecked, like <see cref="Entity"/>.
    /// </summary>
    internal static IEnumerable<Dependency> KeyedUpdateDependencies(
        DbContext context,
        IEntityType entityType,
        IReadOnlyList<object> keys,
        string sql
    ) =>
        ColumnMentions(context, KeyProperty(entityType), sql) > 1
            ? UpdateDependencies(entityType)
            :
            [
                .. keys.Select(Entity(entityType).For),
                .. RowForeignKeys(entityType).Select(foreignKey => ForeignKey(foreignKey).All),
                .. RowUniqueColumns(entityType)
                    .Where(property => ColumnMentions(context, property, sql) > 0)
                    .Select(property => Unique(property).All),
            ];

    /// <summary>
    /// What a delete of the <paramref name="entityType"/> rows with these primary keys may change: each row; every
    /// foreign key a row may have, whole, since their parents are unknown; the rows under each one, through every
    /// <see cref="CascadingForeignKeys"/> foreign key, by the key when it points at the primary key, or whole when it
    /// points at an alternate key, whose value is unknown; and what the database cascades into (<see cref="Cascades"/>).
    /// No unique column: a reader by one that found a row recorded its key, and one that found none still finds none.
    /// Unchecked, like <see cref="Entity"/>.
    /// </summary>
    internal static IEnumerable<Dependency> KeyedDeleteDependencies(
        IEntityType entityType,
        IReadOnlyList<object> keys
    ) =>
        [
            .. keys.Select(Entity(entityType).For),
            .. RowForeignKeys(entityType).Select(foreignKey => ForeignKey(foreignKey).All),
            .. CascadingForeignKeys(entityType)
                .SelectMany(foreignKey =>
                    foreignKey.PrincipalKey.IsPrimaryKey()
                        ? keys.Select(key =>
                            StoredValue(foreignKey.Properties[0], key) is { } principal
                                ? ForeignKey(foreignKey).For(principal)
                                : ForeignKey(foreignKey).All
                        )
                        : [ForeignKey(foreignKey).All]
                ),
            .. Cascades(entityType),
        ];

    // How many times the SQL names the property's columns, as the provider delimits them, each standing alone. A
    // property stored in no column counts as named, many times.
    private static int ColumnMentions(DbContext context, IProperty property, string sql)
    {
        var patterns = property.GetOrAddRuntimeAnnotationValue(
            ColumnPatternsAnnotation,
            static arg =>
                arg.Property.GetTableColumnMappings()
                    .Select(mapping => mapping.Column.Name)
                    .Distinct()
                    .Select(name => NamePattern(arg.Quote.DelimitIdentifier(name)))
                    .ToArray(),
            (Quote: context.GetService<ISqlGenerationHelper>(), Property: property)
        );
        return patterns.Length == 0 ? int.MaxValue : patterns.Sum(pattern => pattern.Count(sql));
    }

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

    /// <summary>
    /// Refuses the model as <see cref="EnsureRoundTrips"/> does, then builds every source it implies: each keyed
    /// entity type's (<see cref="Entity"/>), each foreign key's but an ownership's (<see cref="ForeignKey"/>) and each
    /// unique column's (<see cref="Unique"/>). Once per model, before a save or bulk write on it runs, so a source
    /// name the core refuses (one an app's own <see cref="Source{TKey}"/> took) throws there, before anything is
    /// written, rather than where the write publishes, where a throw would lose the rest of its invalidations.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model has a converter Sluice can't trust.</exception>
    /// <exception cref="ArgumentException">The core refuses one of the model's source names.</exception>
    internal static void EnsureSources(IModel model) =>
        model.GetOrAddRuntimeAnnotationValue(
            SourcesAnnotation,
            static model =>
            {
                EnsureRoundTrips(model!);
                foreach (var entityType in model!.GetEntityTypes())
                {
                    if (!entityType.IsOwned() && entityType.FindPrimaryKey() is not null)
                    {
                        _ = Entity(entityType);
                        foreach (var property in UniqueColumns(entityType))
                        {
                            _ = Unique(property);
                        }
                    }

                    foreach (
                        var foreignKey in entityType
                            .GetDeclaredForeignKeys()
                            .Where(foreignKey => !foreignKey.IsOwnership)
                    )
                    {
                        _ = ForeignKey(foreignKey);
                    }
                }

                return true;
            },
            model
        );

    /// <summary>
    /// Refuses a model with a key, foreign key or unique column (<see cref="UniqueColumns"/>) whose value converter
    /// isn't known to round-trip: neither marked <see cref="SluiceRoundTripsAttribute"/> nor one of EF's built-ins that
    /// gives back every value it can load. A save reports the value it would write for what EF loaded, so a converter
    /// that loads a stored value and writes back another misses the readers of the stored value. Then refuses one whose
    /// date the database stores less precisely than to the microsecond, which rounds what a save writes. Checked once
    /// per model, before any of its sources is built, and before a save or bulk write on it runs.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The model has such a converter or such a date column; the message names each.
    /// </exception>
    internal static void EnsureRoundTrips(IModel model) =>
        model.GetOrAddRuntimeAnnotationValue(
            RoundTripsAnnotation,
            static model =>
            {
                var refused = model!
                    .GetEntityTypes()
                    .SelectMany(entityType => entityType.GetDeclaredProperties())
                    .Where(property =>
                        property.IsKey() || property.IsForeignKey() || IsUniqueColumn(property)
                    )
                    .Select(property => (Property: property, property.GetTypeMapping().Converter))
                    .Where(mapped => mapped.Converter is { } converter && !RoundTrips(converter))
                    .Select(mapped =>
                        $"{mapped.Property.DeclaringType.DisplayName()}.{mapped.Property.Name} "
                        + $"({DisplayName(mapped.Converter!.GetType())})"
                    )
                    .ToList();
                if (refused.Count == 0)
                {
                    EnsurePrecise(model);
                    return true;
                }

                throw new InvalidOperationException(
                    $"Sluice can't trust the value converter of {string.Join(", ", refused)}: a key, foreign key "
                        + "or unique column. A save invalidates the value it would write back for what EF loaded, so a "
                        + "converter that loads a stored value and writes back another (\"01\" loaded as 1 and written as "
                        + "\"1\", a GUID stored with braces written without them, \" Active\" parsed as an enum) "
                        + "leaves the readers of what the database held serving stale data. If converting every "
                        + "stored value to .NET and back gives that same value, mark the converter's class "
                        + "[SluiceRoundTrips]; one from HasConversion(toProvider, fromProvider) has no class, so move "
                        + "its two conversions into a class deriving from ValueConverter<TModel, TProvider> and mark "
                        + "that, and to vouch for one you don't own, derive from it and mark the derived class. If it "
                        + "doesn't, make it round-trip, or store the value without a converter."
                );
            },
            model
        );

    // A date stored less precisely than the microsecond Sluice folds dates to (SQL Server's datetime, about 3 ms, and
    // smalldatetime, a minute; a date column; a precision under 6) holds a value other than the one a save reports for
    // a new row, or a reader asks by, so the two give the row different key text and an eviction is missed.
    private static void EnsurePrecise(IModel model)
    {
        var refused = model
            .GetEntityTypes()
            .SelectMany(entityType => entityType.GetDeclaredProperties())
            .Where(property =>
                property.IsKey() || property.IsForeignKey() || IsUniqueColumn(property)
            )
            .Select(property => (Property: property, Mapping: property.FindRelationalTypeMapping()))
            .Where(mapped => mapped.Mapping is { } mapping && Imprecise(mapped.Property, mapping))
            .Select(mapped =>
                $"{mapped.Property.DeclaringType.DisplayName()}.{mapped.Property.Name} ({mapped.Mapping!.StoreType})"
            )
            .ToList();
        if (refused.Count > 0)
        {
            throw new InvalidOperationException(
                $"Sluice can't key {string.Join(", ", refused)}: a key, foreign key or unique column whose date is "
                    + "stored less precisely than to the microsecond. The database rounds what a save writes, so a new "
                    + "row's key, and a key a read asks by, isn't the value the row holds, and an eviction is missed. "
                    + "Store it to the microsecond or better (on SQL Server datetime2, on PostgreSQL timestamp without "
                    + "a precision under 6), or key the type by another column."
            );
        }
    }

    private static readonly HashSet<string> _impreciseDates = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime",
        "smalldatetime",
        "date",
    };

    private static bool Imprecise(IProperty property, RelationalTypeMapping mapping)
    {
        var provider = mapping.Converter?.ProviderClrType ?? mapping.ClrType;
        var stored = Nullable.GetUnderlyingType(provider) ?? provider;
        return (stored == typeof(DateTime) || stored == typeof(DateTimeOffset))
            && (
                _impreciseDates.Contains(mapping.StoreTypeNameBase)
                || (property.GetPrecision() ?? mapping.Precision) is < 6
            );
    }

    // EF's built-ins that give back every value they can load. An enum or number cast into a type that holds every
    // value the column can (int into long, float into double, any integer into decimal), and DateTime ticks and Guid
    // bytes, which construct exactly what they read or throw. Off the list: everything that parses text, which accepts
    // more than one spelling of a value; casts that narrow (a long column read as int, a decimal as long) or round;
    // DateTimeToBinaryConverter, whose stored kind bits have spellings ToBinary never writes; and the bool converters,
    // which read every value but the true one as false.
    private static bool RoundTrips(ValueConverter converter)
    {
        var type = converter.GetType();
        if (
            type.IsDefined(typeof(SluiceRoundTripsAttribute), inherit: false)
            || type == typeof(DateTimeToTicksConverter)
            || type == typeof(GuidToBytesConverter)
        )
        {
            return true;
        }

        return type.IsGenericType
            && type.GetGenericTypeDefinition() is var definition
            && (
                definition == typeof(EnumToNumberConverter<,>)
                || definition == typeof(CastingConverter<,>)
            )
            && Holds(Number(converter.ModelClrType), Number(converter.ProviderClrType));
    }

    // The numeric type a value is stored as: its own, or an enum's underlying one.
    private static Type Number(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum ? Enum.GetUnderlyingType(underlying) : underlying;
    }

    private static readonly Dictionary<Type, (decimal Min, decimal Max)> _integers = new()
    {
        [typeof(sbyte)] = (sbyte.MinValue, sbyte.MaxValue),
        [typeof(byte)] = (byte.MinValue, byte.MaxValue),
        [typeof(short)] = (short.MinValue, short.MaxValue),
        [typeof(ushort)] = (ushort.MinValue, ushort.MaxValue),
        [typeof(int)] = (int.MinValue, int.MaxValue),
        [typeof(uint)] = (uint.MinValue, uint.MaxValue),
        [typeof(long)] = (long.MinValue, long.MaxValue),
        [typeof(ulong)] = (ulong.MinValue, ulong.MaxValue),
    };

    // Whether the model type holds every value of the provider type exactly, so a cast there and back gives it back.
    private static bool Holds(Type model, Type provider) =>
        model == provider
        || (
            _integers.TryGetValue(provider, out var stored)
            && (
                model == typeof(decimal)
                || (
                    _integers.TryGetValue(model, out var held)
                    && held.Min <= stored.Min
                    && stored.Max <= held.Max
                )
            )
        )
        || (provider == typeof(float) && model == typeof(double));

    private static string DisplayName(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(DisplayName))}>"
            : type.Name;

    // A composite key, which only writes produce, arrives as an array of its values in key order. Every source is
    // built here, so none is built for a model whose converters Sluice can't trust.
    private static Source<object> Build(
        IEntityType entityType,
        string name,
        IReadOnlyList<IProperty> properties
    )
    {
        EnsureRoundTrips(entityType.Model);
        return properties is [var property]
            ? Sluice.Infrastructure.ModelSource.Create<object>(
                name,
                key => KeyFolding.Fold(ToProvider(property, key))
            )
            : Sluice.Infrastructure.ModelSource.Create<object>(
                name,
                key =>
                    JsonSerializer.Serialize(
                        ((object[])key)
                            .Select((value, i) => KeyFolding.Fold(ToProvider(properties[i], value)))
                            .ToArray()
                    )
            );
    }

    /// <summary>
    /// <paramref name="value"/>, of <paramref name="from"/>, as <paramref name="to"/> reads what it's stored as: the two
    /// properties map one column in different types (table splitting). Null when it's stored as null.
    /// </summary>
    internal static object? ValueAs(IProperty from, IProperty to, object value)
    {
        var stored = from.GetTypeMapping().Converter is { } converter
            ? converter.ConvertToProvider(value)
            : value;
        return stored is not null && to.GetTypeMapping().Converter is { } back
            ? back.ConvertFromProvider(stored)
            : stored;
    }

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
