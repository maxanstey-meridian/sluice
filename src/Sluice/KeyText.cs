using System.Globalization;

namespace Sluice;

/// <summary>
/// Turns keys into text (DESIGN §4.3, D12): <c>string</c> as is, <see cref="IFormattable"/> invariant,
/// anything else through a format function. Query entry keys need this to be injective.
/// </summary>
internal static class KeyText
{
    internal static Func<TKey, string> Default<TKey>()
        where TKey : notnull
    {
        var type = typeof(TKey);

        // Their invariant text keeps whole seconds (TimeOnly: minutes), so different keys would share an entry.
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeOnly))
        {
            throw new ArgumentException(
                $"Key type {type.Name} loses precision in its default text, so two different keys could share an entry. "
                    + "Pass a format function, for example format: key => key.ToString(\"O\", CultureInfo.InvariantCulture), "
                    + "after normalising date-times to UTC."
            );
        }

        if (type == typeof(string))
        {
            return key => (string)(object)key;
        }

        if (typeof(IFormattable).IsAssignableFrom(type))
        {
            return key => ((IFormattable)key).ToString(null, CultureInfo.InvariantCulture);
        }

        throw new ArgumentException(
            $"Key type {type.Name} is neither string nor IFormattable, so Sluice can't turn it into text. "
                + "Pass a format function that gives different text for different keys: new Source<T>(name, key => ...)."
        );
    }

    internal static string Of<TKey>(Func<TKey, string> format, TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return format(key)
            ?? throw new InvalidOperationException(
                $"The format function returned null for key '{key}'. Return a non-null string for every key."
            );
    }
}
