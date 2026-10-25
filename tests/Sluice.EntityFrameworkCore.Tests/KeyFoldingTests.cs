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
    public void Durations_fold_to_the_nearest_second()
    {
        var duration = TimeSpan.FromSeconds(90);

        KeyFolding
            .Fold(duration + TimeSpan.FromMilliseconds(499.9999))
            .Should()
            .Be(KeyFolding.Fold(duration), "MySQL's time without a size rounds to the second");
        KeyFolding
            .Fold(duration + TimeSpan.FromMilliseconds(500))
            .Should()
            .Be(KeyFolding.Fold(duration + TimeSpan.FromSeconds(1)));
        KeyFolding
            .Fold(duration + TimeSpan.FromSeconds(1))
            .Should()
            .NotBe(KeyFolding.Fold(duration));
        KeyFolding.Fold(-duration).Should().NotBe(KeyFolding.Fold(duration));
        KeyFolding
            .Fold(-(duration + TimeSpan.FromMilliseconds(500)))
            .Should()
            .Be(KeyFolding.Fold(-(duration + TimeSpan.FromSeconds(1))), "half away from zero");
    }

    [Fact]
    public void Times_of_day_fold_to_the_nearest_second()
    {
        var noon = new TimeOnly(12, 0);

        KeyFolding
            .Fold(noon.Add(TimeSpan.FromMilliseconds(400)))
            .Should()
            .Be(KeyFolding.Fold(noon));
        KeyFolding
            .Fold(noon.Add(TimeSpan.FromSeconds(59.6)))
            .Should()
            .Be(
                KeyFolding.Fold(new TimeOnly(12, 1)),
                "MySQL's time without a size rounds to the second"
            );
        KeyFolding.Fold(noon.Add(TimeSpan.FromSeconds(1))).Should().NotBe(KeyFolding.Fold(noon));
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
