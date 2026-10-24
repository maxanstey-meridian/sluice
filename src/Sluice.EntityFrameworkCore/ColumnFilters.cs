using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The conditions and projections Sluice adds to a query by one column of the EF model, named through
/// <c>EF.Property</c> so a shadow property works too, with the values captured so EF sends them as parameters.
/// </summary>
internal static class ColumnFilters
{
    private static readonly MethodInfo _efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo _objectEquals = typeof(object).GetMethod(
        nameof(Equals),
        [typeof(object), typeof(object)]
    )!;
    private static readonly MethodInfo _contains = typeof(Enumerable)
        .GetMethods()
        .Single(method =>
            method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2
        );

    /// <summary>
    /// <c>e =&gt; Equals(EF.Property&lt;TKey&gt;(e, name), (TKey)value)</c>. <c>object.Equals</c>, as EF's own key
    /// lookups use, because a converted key type needn't define <c>==</c>.
    /// </summary>
    internal static IQueryable<T> WhereEquals<T>(
        IQueryable<T> query,
        IProperty property,
        object value
    )
        where T : class
    {
        var entity = Expression.Parameter(typeof(T), "e");
        Expression<Func<object>> captured = () => value;
        var equals = Expression.Call(
            _objectEquals,
            Expression.Convert(Column(entity, property), typeof(object)),
            Expression.Convert(Expression.Convert(captured.Body, property.ClrType), typeof(object))
        );

        return query.Where(Expression.Lambda<Func<T, bool>>(equals, entity));
    }

    /// <summary>
    /// <c>e =&gt; values.Contains(EF.Property&lt;TKey&gt;(e, name))</c>, the values in an array of the column's CLR
    /// type, which EF sends as one parameter.
    /// </summary>
    internal static IQueryable<T> WhereIn<T>(
        IQueryable<T> query,
        IProperty property,
        IReadOnlyList<object> values
    )
        where T : class
    {
        var array = Array.CreateInstance(property.ClrType, values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            array.SetValue(values[i], i);
        }

        var entity = Expression.Parameter(typeof(T), "e");
        Expression<Func<object>> captured = () => array;
        var contains = Expression.Call(
            _contains.MakeGenericMethod(property.ClrType),
            Expression.Convert(captured.Body, array.GetType()),
            Column(entity, property)
        );

        return query.Where(Expression.Lambda<Func<T, bool>>(contains, entity));
    }

    /// <summary><c>e =&gt; (object)EF.Property&lt;TKey&gt;(e, name)</c>, for a projection of one column.</summary>
    internal static Expression<Func<T, object>> Select<T>(IProperty property)
        where T : class
    {
        var entity = Expression.Parameter(typeof(T), "e");
        return Expression.Lambda<Func<T, object>>(
            Expression.Convert(Column(entity, property), typeof(object)),
            entity
        );
    }

    private static MethodCallExpression Column(ParameterExpression entity, IProperty property) =>
        Expression.Call(
            _efProperty.MakeGenericMethod(property.ClrType),
            entity,
            Expression.Constant(property.Name)
        );
}
