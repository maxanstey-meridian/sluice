using Microsoft.EntityFrameworkCore.Metadata;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The rows a keyed bulk write (<c>ForKey</c>, <c>ForKeys</c>) can touch, set on the flow around EF's call so
/// <see cref="SluiceInterceptor"/> can narrow what the statement publishes to those rows. The keys are the CLR values
/// Sluice put in the statement's <c>WHERE</c> itself, so they bound what it changed, whatever else the app's own
/// <c>Where</c> says.
/// </summary>
internal sealed class KeyPin
{
    private static readonly AsyncLocal<KeyPin?> _current = new();

    private int _taken;

    private KeyPin(IEntityType entityType, IReadOnlyList<object> keys)
    {
        EntityType = entityType;
        Keys = keys;
    }

    /// <summary>The type the write was keyed on, in the model of the query it was keyed from.</summary>
    internal IEntityType EntityType { get; }

    /// <summary>The primary keys, each of the key property's CLR type.</summary>
    internal IReadOnlyList<object> Keys { get; }

    /// <summary>
    /// Pins the keys on the current flow until the returned scope is disposed, which puts back what the flow had: a sync
    /// call sets the flow's own value, which would otherwise outlive it.
    /// </summary>
    internal static Scope Around(IEntityType entityType, IReadOnlyList<object> keys)
    {
        var previous = _current.Value;
        _current.Value = new(entityType, keys);
        return new(previous);
    }

    /// <summary>
    /// The flow's pin, for the first bulk command EF initialises while the keyed call runs: the statement it runs for
    /// the call. Any later command in the same call (an interceptor's own bulk write, or the execution strategy's
    /// retry) gets none, and publishes whole, which only over-invalidates.
    /// </summary>
    internal static KeyPin? Take() =>
        _current.Value is { } pin && Interlocked.Exchange(ref pin._taken, 1) == 0 ? pin : null;

    internal readonly struct Scope(KeyPin? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
