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

    // PostgreSQL stores date-times to the microsecond.
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
            TimeSpan duration => NearestSecond(duration.Ticks),
            byte[] bytes => Convert.ToHexString(bytes),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static string WholeMicroseconds(long ticks) =>
        (ticks - ticks % TicksPerMicrosecond).ToString(CultureInfo.InvariantCulture);

    // A duration column keeps at least whole seconds: MySQL's time without a size rounds to them, PostgreSQL's interval
    // keeps microseconds, cut by Npgsql, and SQL Server's time keeps 100 ns. Rounding to the nearest second gives a
    // value and what each of them stores the same text: a half second is a whole microsecond, so cutting to the
    // microsecond never crosses one. Durations within a second of each other may share a key, which only
    // over-invalidates.
    private static string NearestSecond(long ticks) =>
        Math.Round((decimal)ticks / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero)
            .ToString(CultureInfo.InvariantCulture);
}
