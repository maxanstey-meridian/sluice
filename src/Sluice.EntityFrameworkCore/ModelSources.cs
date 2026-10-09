using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The core sources an EF model implies: one per root entity type, named by EF so TPH and TPT reads and writes
/// agree, and one per foreign key. Each is built once per model, through the core's public <c>format</c>
/// overload, and kept as a runtime annotation on the model.
/// </summary>
internal static class ModelSources
{
    private const string SourceAnnotation = "Sluice:Source";

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

    /// <summary>The single-column primary key of <paramref name="entityType"/>, which <see cref="Entity"/> has checked.</summary>
    /// <exception cref="NotSupportedException">The primary key is composite.</exception>
    internal static IProperty KeyProperty(IEntityType entityType) =>
        entityType.FindPrimaryKey()!.Properties is [var property]
            ? property
            : throw new NotSupportedException(
                $"{entityType.DisplayName()} has a composite primary key. Reading or invalidating one row by "
                    + "composite key isn't supported; read it with read.All or invalidate it with AllDependencies()."
            );

    /// <summary>
    /// Resolves <paramref name="selector"/> (an FK property, <c>EF.Property</c> on a shadow FK, or a reference
    /// navigation) to a single-column foreign key of <paramref name="entityType"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The selector doesn't name a single-column foreign key.</exception>
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
                Arguments: [ParameterExpression, ConstantExpression { Value: string property }],
            } call when call.Method.DeclaringType == typeof(EF) => property,
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
                    + "property (o => o.CustomerId), a shadow FK (o => EF.Property<int>(o, \"CustomerId\")) or a "
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
        var reason =
            entityType.FindPrimaryKey() is null ? "is keyless"
            : entityType.GetViewName() is { } view ? $"is mapped to the view '{view}'"
            : entityType.GetFunctionName() is { } function
                ? $"is mapped to the function '{function}'"
            : entityType.GetSqlQuery() is not null ? "is mapped to a SQL query"
            : entityType.GetTableName() is null ? "isn't mapped to a table"
            : null;

        if (reason is not null)
        {
            throw new NotSupportedException(
                $"{entityType.DisplayName()} {reason}, so no tracked write can invalidate a cached read of it. "
                    + "Read it through read.From with a declared Source, and invalidate that source after it changes."
            );
        }
    }

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
