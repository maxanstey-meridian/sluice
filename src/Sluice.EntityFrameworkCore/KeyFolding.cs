using System.Globalization;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Turns a provider value into key text so that values the database treats as equal get the same text.
/// Folding can merge values the database tells apart, which only over-invalidates.
/// </summary>
/// <remarks>
/// Accent- and culture-sensitive string collations aren't folded: two keys equal under such a collation can get
/// different text, so a write to one wouldn't evict a reader of the other.
/// </remarks>
internal static class KeyFolding
{
    // Dividing by one at the maximum scale leaves the smallest scale that holds the value, so 1.00m becomes 1m.
    private const decimal ScaleNormaliser = 1.0000000000000000000000000000m;

    // PostgreSQL stores date-times, durations and times of day to the microsecond, and every connector cuts to it.
    private const long TicksPerMicrosecond = 10;

    internal static string Fold(object value) =>
        value switch
        {
            // Case-insensitive (citext) and blank-padded (char(n)) comparisons.
            string text => text.TrimEnd().ToUpperInvariant(),
            decimal number => (number / ScaleNormaliser).ToString(CultureInfo.InvariantCulture),
            DateTime instant => WholeMicroseconds(
                instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime().Ticks : instant.Ticks
            ),
            DateTimeOffset instant => WholeMicroseconds(instant.UtcTicks),
            TimeSpan duration => WholeMicroseconds(duration.Ticks),
            TimeOnly time => WholeMicroseconds(time.Ticks),
            byte[] bytes => Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static string WholeMicroseconds(long ticks) =>
        (ticks - ticks % TicksPerMicrosecond).ToString(CultureInfo.InvariantCulture);
}
