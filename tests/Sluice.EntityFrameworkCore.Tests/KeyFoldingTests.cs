using System.Globalization;

namespace Sluice.EntityFrameworkCore.Tests;

public sealed class KeyFoldingTests
{
    [Fact]
    public void Decimals_equal_in_value_fold_alike_whatever_their_scale()
    {
        KeyFolding.Fold(1.00m).Should().Be(KeyFolding.Fold(1m));
        KeyFolding.Fold(-2.500m).Should().Be(KeyFolding.Fold(-2.5m));
        KeyFolding.Fold(0.00m).Should().Be(KeyFolding.Fold(0m));
        KeyFolding.Fold(1.5m).Should().NotBe(KeyFolding.Fold(1.05m));
        KeyFolding.Fold(100m).Should().NotBe(KeyFolding.Fold(1m));
    }

    [Fact]
    public void Strings_differing_only_in_case_or_trailing_blanks_fold_alike()
    {
        KeyFolding.Fold("Widget").Should().Be(KeyFolding.Fold("WIDGET"));
        KeyFolding.Fold("widget  ").Should().Be(KeyFolding.Fold("WIDGET"));
        KeyFolding.Fold(" widget").Should().NotBe(KeyFolding.Fold("widget"));
        KeyFolding.Fold("widget").Should().NotBe(KeyFolding.Fold("gadget"));
    }

    [Fact]
    public void Date_times_fold_to_whole_microseconds_of_the_same_instant()
    {
        var utc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc).AddTicks(12_345_670);

        KeyFolding
            .Fold(utc.AddTicks(9))
            .Should()
            .Be(KeyFolding.Fold(utc), "PostgreSQL keeps microseconds");
        KeyFolding.Fold(utc.AddTicks(10)).Should().NotBe(KeyFolding.Fold(utc));
        KeyFolding.Fold(utc.ToLocalTime()).Should().Be(KeyFolding.Fold(utc));
        KeyFolding
            .Fold(new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(9)))
            .Should()
            .Be(KeyFolding.Fold(new DateTimeOffset(utc)));
    }

    [Fact]
    public void Durations_and_times_of_day_fold_to_whole_microseconds()
    {
        var duration = TimeSpan.FromSeconds(90).Add(TimeSpan.FromTicks(1_234_560));
        var time = new TimeOnly(12, 0).Add(TimeSpan.FromTicks(1_234_560));

        KeyFolding
            .Fold(duration + TimeSpan.FromTicks(9))
            .Should()
            .Be(KeyFolding.Fold(duration), "no connector sends less than the microsecond");
        KeyFolding
            .Fold(duration + TimeSpan.FromTicks(10))
            .Should()
            .NotBe(KeyFolding.Fold(duration));
        KeyFolding
            .Fold(-(duration + TimeSpan.FromTicks(9)))
            .Should()
            .Be(KeyFolding.Fold(-duration), "cut toward zero, as Npgsql and MySQL's connector do");
        KeyFolding.Fold(-duration).Should().NotBe(KeyFolding.Fold(duration));
        KeyFolding.Fold(time.Add(TimeSpan.FromTicks(9))).Should().Be(KeyFolding.Fold(time));
        KeyFolding.Fold(time.Add(TimeSpan.FromTicks(10))).Should().NotBe(KeyFolding.Fold(time));
    }

    [Fact]
    public void Byte_arrays_fold_by_content()
    {
        KeyFolding
            .Fold(new byte[] { 0x0A, 0xFF })
            .Should()
            .Be(KeyFolding.Fold(new byte[] { 0x0A, 0xFF }));
        KeyFolding
            .Fold(new byte[] { 0x0A, 0xFF })
            .Should()
            .NotBe(KeyFolding.Fold(new byte[] { 0x0A }));
    }

    [Fact]
    public void Other_formattable_values_fold_with_the_invariant_culture()
    {
        var current = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            KeyFolding.Fold(1.5d).Should().Be("1.5");
            KeyFolding.Fold(1234).Should().Be("1234");
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
        }
    }
}
