using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Sluice.EntityFrameworkCore;

namespace Sluice;

/// <summary>
/// The <typeparamref name="T"/> rows a query selects, narrowed to the primary keys given to
/// <see cref="KeyedRowsExtensions.ForKey{T}(IQueryable{T}, object)"/> or
/// <see cref="KeyedRowsExtensions.ForKeys{T, TKey}(IQueryable{T}, IEnumerable{TKey})"/>, for a bulk write: once it
/// commits, Sluice evicts only those rows' readers, where a plain <c>ExecuteUpdate</c> or <c>ExecuteDelete</c> evicts
/// every read of the type. It only executes, so it can't end up inside another query.
/// </summary>
/// <remarks>
/// Either also evicts every <c>read.Children</c> list of the type's foreign keys; an update, every <c>read.EntityAsync</c>
/// reader by a unique column whose name appears in the statement; a delete, the lists under each deleted row and what
/// the database cascades into. Other types the statement names are evicted whole, and so is this one past 100 keys, or
/// when an update's statement names the key's column again (setting the key, or a join on another table's column of that
/// name).
/// The rules in full: <see href="https://maxanstey-meridian.github.io/sluice/guides/ef-core">EF Core</see>.
/// </remarks>
/// <typeparam name="T">The entity type the rows are of.</typeparam>
public sealed class KeyedRows<T>
    where T : class
{
    private readonly IQueryable<T> _rows;
    private readonly IEntityType _entityType;
    private readonly IReadOnlyList<object> _keys;

    private KeyedRows(IQueryable<T> rows, IEntityType entityType, IReadOnlyList<object> keys)
    {
        _rows = rows;
        _entityType = entityType;
        _keys = keys;
    }

    /// <summary>
    /// Updates the rows, as EF's <c>ExecuteUpdate</c> does, and evicts only their readers once the update commits.
    /// </summary>
    /// <param name="setPropertyCalls">The columns to set, as EF's <c>ExecuteUpdate</c> takes them.</param>
    /// <returns>The number of rows updated; 0, without running anything, when no keys were given.</returns>
    public int ExecuteUpdate(Action<UpdateSettersBuilder<T>> setPropertyCalls)
    {
        ArgumentNullException.ThrowIfNull(setPropertyCalls);
        if (_keys.Count == 0)
        {
            return 0;
        }

        using var pin = KeyPin.Around(_entityType, _keys);
        return _rows.ExecuteUpdate(setPropertyCalls);
    }

    /// <summary>
    /// Updates the rows, as EF's <c>ExecuteUpdateAsync</c> does, and evicts only their readers once the update
    /// commits.
    /// </summary>
    /// <param name="setPropertyCalls">The columns to set, as EF's <c>ExecuteUpdateAsync</c> takes them.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The number of rows updated; 0, without running anything, when no keys were given.</returns>
    public async Task<int> ExecuteUpdateAsync(
        Action<UpdateSettersBuilder<T>> setPropertyCalls,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(setPropertyCalls);
        if (_keys.Count == 0)
        {
            return 0;
        }

        using var pin = KeyPin.Around(_entityType, _keys);
        return await _rows
            .ExecuteUpdateAsync(setPropertyCalls, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the rows, as EF's <c>ExecuteDelete</c> does, and evicts only their readers, and what the database
    /// cascades into, once the delete commits.
    /// </summary>
    /// <returns>The number of rows deleted; 0, without running anything, when no keys were given.</returns>
    public int ExecuteDelete()
    {
        if (_keys.Count == 0)
        {
            return 0;
        }

        using var pin = KeyPin.Around(_entityType, _keys);
        return _rows.ExecuteDelete();
    }

    /// <summary>
    /// Deletes the rows, as EF's <c>ExecuteDeleteAsync</c> does, and evicts only their readers, and what the database
    /// cascades into, once the delete commits.
    /// </summary>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The number of rows deleted; 0, without running anything, when no keys were given.</returns>
    public async Task<int> ExecuteDeleteAsync(CancellationToken cancellationToken = default)
    {
        if (_keys.Count == 0)
        {
            return 0;
        }

        using var pin = KeyPin.Around(_entityType, _keys);
        return await _rows.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    // The rows the query selects with the key condition ANDed on, so the statement can only touch those keys. The
    // query's entity type comes from the model its root carries; EF's own queryable and provider types are internal.
    internal static KeyedRows<T> Of(IQueryable<T> rows, object[] keys, string parameterName)
    {
        var entityType =
            Root.Of(rows.Expression)?.EntityType.Model.FindEntityType(typeof(T))
            ?? throw new ArgumentException(
                $"ForKey and ForKeys need an EF query of an entity type, such as db.Set<{typeof(T).Name}>().Where(…), "
                    + $"and this query of {typeof(T).Name} isn't one.",
                nameof(rows)
            );
        ModelSources.EnsureTable(entityType);
        var key = ModelSources.KeyProperty(entityType);
        foreach (var value in keys)
        {
            ModelSources.EnsureKeyType(key, value, parameterName);
        }

        return new(
            keys switch
            {
                [] => rows,
                [var single] => ColumnFilters.WhereEquals(rows, key, single),
                _ => ColumnFilters.WhereIn(rows, key, keys),
            },
            entityType,
            keys
        );
    }

    /// <summary>The first EF entity root in a query's expression, which every EF query bottoms out in.</summary>
    private sealed class Root : ExpressionVisitor
    {
        private EntityQueryRootExpression? _found;

        internal static EntityQueryRootExpression? Of(Expression query)
        {
            var root = new Root();
            root.Visit(query);
            return root._found;
        }

        public override Expression? Visit(Expression? node)
        {
            if (_found is not null || node is null)
            {
                return node;
            }

            if (node is EntityQueryRootExpression root)
            {
                _found = root;
                return node;
            }

            return base.Visit(node);
        }
    }
}
