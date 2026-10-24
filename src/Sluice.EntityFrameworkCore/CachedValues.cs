using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Refuses a computed value that holds a lazy loader or a <see cref="DbContext"/> anywhere in it, before the cache
/// stores it (SLUICE-EF-027): an entity that loads lazily, wrapped in the app's own type, would read related rows later,
/// outside the compute and with no dependency recorded, and a context would stay alive, with what it tracks, for every
/// caller. The analysis refuses such entities by the query's result type (SLUICE-EF-016); this finds them in the value,
/// however it's built.
/// </summary>
/// <remarks>
/// A walk of the value's fields, never its properties, so none of the app's code runs, with an explicit stack and a
/// set of what it has visited by reference. Each runtime type's fields worth following are worked out once: those
/// whose type may hold a loader or a context. Primitives, strings, enums and reflection objects are skipped, as are
/// structs and sealed types (and arrays of them) whose fields can't hold one, so a list of sealed records costs one
/// look at the list. It's off until the analysis has seen a model whose entities load lazily or hold their context,
/// since only EF injects either; a model seen once keeps it on for the process.
/// </remarks>
internal static class CachedValues
{
    private const string SeenAnnotation = "Sluice:HoldsLoaders";

    private static volatile bool _on;

    // The fields of each runtime type to follow; null when there are none.
    private static readonly ConcurrentDictionary<Type, FieldInfo[]?> _plans = new();

    // Whether no value of a static type can hold a loader or a context, for the types worked out to the end.
    private static readonly ConcurrentDictionary<Type, bool> _closed = new();

    /// <summary>
    /// Turns the check on if <paramref name="model"/> has entity types EF gives a lazy loader or their context, once
    /// per model. Called by the analysis whenever EF compiles a query, before it runs.
    /// </summary>
    internal static void Watch(IModel model)
    {
        if (
            !_on
            && model.GetOrAddRuntimeAnnotationValue(
                SeenAnnotation,
                static model =>
                    model!.GetEntityTypes().Any(type => TrackedReads.HoldsServices(type)),
                model
            )
        )
        {
            _on = true;
        }
    }

    /// <summary>
    /// The store check the EF package adds to each cache: why <paramref name="value"/> mustn't be stored, or null.
    /// </summary>
    internal static Exception? Check(object value)
    {
        if (!_on || Find(value) is not ({ } found, var holder))
        {
            return null;
        }

        var query = ActiveCompute.Reads is { } reads ? $"'{ActiveCompute.QueryName(reads)}'" : "A";
        var where = holder is null
            ? $"as the value itself ({Display(found.GetType())})"
            : $"in a {Display(holder.GetType())} at {PathTo(value, found)}";
        return new InvalidOperationException(
            found is DbContext
                ? $"Query {query} returned a value holding a DbContext {where}, so the cached value would keep that "
                    + "context, and what it tracks, alive and shared by every caller. Select the values you need into "
                    + "your own type instead of the entity."
                : $"Query {query} returned a value holding a lazy loader {where}, so the cached value would load "
                    + "related rows later, outside the compute and with no dependency recorded. Select the values you "
                    + "need into your own type instead of the entity."
        );
    }

    // The first loader or context the walk reaches, and the object whose field or element holds it (null for the
    // value itself).
    private static (object Found, object? Holder)? Find(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(object Value, object? Holder)>();
        stack.Push((root, null));
        while (stack.TryPop(out var next))
        {
            var (value, holder) = next;
            if (value is ILazyLoader or DbContext)
            {
                return (value, holder);
            }

            if (!seen.Add(value))
            {
                continue;
            }

            if (value is Array array)
            {
                if (!Closed(array.GetType().GetElementType()!))
                {
                    foreach (var item in array)
                    {
                        if (item is not null)
                        {
                            stack.Push((item, value));
                        }
                    }
                }

                continue;
            }

            foreach (var field in Plan(value.GetType()) ?? [])
            {
                if (field.GetValue(value) is { } inner)
                {
                    stack.Push((inner, value));
                }
            }
        }

        return null;
    }

    // Where the walk found it, for the message: worked out again only for a value that's refused.
    private static string PathTo(object root, object target)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Value, string Path)>();
        queue.Enqueue((root, Display(root.GetType())));
        while (queue.TryDequeue(out var next))
        {
            var (value, path) = next;
            if (ReferenceEquals(value, target))
            {
                return path;
            }

            if (value is ILazyLoader or DbContext || !seen.Add(value))
            {
                continue;
            }

            if (value is Array array)
            {
                var i = 0;
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        queue.Enqueue((item, $"{path}[{i}]"));
                    }

                    i++;
                }

                continue;
            }

            foreach (var field in Plan(value.GetType()) ?? [])
            {
                if (field.GetValue(value) is { } inner)
                {
                    queue.Enqueue((inner, $"{path}.{FieldName(field)}"));
                }
            }
        }

        return Display(root.GetType());
    }

    private static FieldInfo[]? Plan(Type type) =>
        _plans.GetOrAdd(
            type,
            static type =>
                Closed(type) ? null
                : InstanceFields(type).Where(field => !Closed(field.FieldType)).ToArray()
                    is { Length: > 0 } fields
                    ? fields
                : null
        );

    private static bool Closed(Type type)
    {
        if (_closed.TryGetValue(type, out var known))
        {
            return known;
        }

        var closed = Closed(type, []);
        _closed.TryAdd(type, closed);
        return closed;
    }

    // A static type no value of which can hold a loader or a context: a primitive, an enum, a string, a pointer, a
    // reflection object, or a struct or sealed type (or an array of one) whose fields are all such. A type met again
    // while it's being worked out is taken as closed, which is right for the type the work started from, since its
    // other fields are still looked at, but not always for the ones in between, so only the first type's answer is
    // kept.
    private static bool Closed(Type type, HashSet<Type> working)
    {
        if (
            type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type.IsPointer
            || type.IsFunctionPointer
            || typeof(MemberInfo).IsAssignableFrom(type)
            || typeof(Assembly).IsAssignableFrom(type)
            || typeof(Module).IsAssignableFrom(type)
            || typeof(ParameterInfo).IsAssignableFrom(type)
        )
        {
            return true;
        }

        if (type.IsArray)
        {
            return Closed(type.GetElementType()!, working);
        }

        if (
            typeof(DbContext).IsAssignableFrom(type)
            || typeof(ILazyLoader).IsAssignableFrom(type)
            || (!type.IsValueType && !type.IsSealed)
        )
        {
            return false;
        }

        if (_closed.TryGetValue(type, out var known))
        {
            return known;
        }

        return !working.Add(type)
            || InstanceFields(type).All(field => Closed(field.FieldType, working));
    }

    private static IEnumerable<FieldInfo> InstanceFields(Type type)
    {
        for (var declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            foreach (
                var field in declaring.GetFields(
                    BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
            )
            {
                yield return field;
            }
        }
    }

    // An auto-property's backing field is named after the property.
    private static string FieldName(FieldInfo field) =>
        field.Name is ['<', .. var rest] && rest.IndexOf('>') is > 0 and var end
            ? rest[..end]
            : field.Name;

    private static string Display(Type type)
    {
        if (!type.IsGenericType || type.Name.IndexOf('`') is not (> 0 and var tick))
        {
            return type.Name;
        }

        var name = new StringBuilder(type.Name[..tick]).Append('<');
        name.AppendJoin(", ", type.GetGenericArguments().Select(Display));
        return name.Append('>').ToString();
    }
}
