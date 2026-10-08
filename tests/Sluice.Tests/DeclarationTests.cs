using System.Globalization;
using Sluice.Tests.Support;

namespace Sluice.Tests;

public sealed class DeclarationTests
{
    public sealed record CustomerId(string Value);

    // A second type named CustomerId, in another namespace scope.
    public static class Other
    {
        public sealed record CustomerId(string Value);
    }

    // A user IFormattable whose text differs for every key.
    public readonly record struct Sku(int Value) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) =>
            Value.ToString(formatProvider);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Blank_source_names_are_rejected(string name)
    {
        var declare = () => new Source<int>(name);

        declare.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Blank_query_names_are_rejected(string name)
    {
        var declare = () => new Query<int, string>(name);

        declare.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Query_names_containing_a_colon_are_rejected()
    {
        var declare = () => new Query<int, string>("statement:v2");

        declare.Should().Throw<ArgumentException>().WithMessage("*':'*");
    }

    // FusionCache keeps tag data under "__fc:t:<tag>" (or "__fc-t-<tag>" after SetToSafeStrings) in the
    // entries' key space and treats any key there as tag data.
    [Theory]
    [InlineData("__fc")]
    [InlineData("__fc-t-decl-rows")]
    public void Query_names_starting_with_FusionCache_s_internal_prefix_are_rejected(string name)
    {
        var declare = () => new Query<string, long>(name);

        declare.Should().Throw<ArgumentException>().WithMessage("*FusionCache uses*");
    }

    [Fact]
    public void Without_a_format_a_key_type_that_is_neither_string_nor_IFormattable_is_rejected()
    {
        new Func<object>(() => new Source<CustomerId>("decl-record-source"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*CustomerId*format*");
        new Func<object>(() => new Query<CustomerId, string>("decl-record-query"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*CustomerId*format*");
    }

    [Fact]
    public void Without_a_format_interface_and_abstract_key_types_are_rejected()
    {
        // The runtime type would decide the text: int 1 and long 1 would share an entry, and a DateTime key
        // would skip the precision check.
        new Func<object>(() => new Source<IFormattable>("decl-interface-source"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*IFormattable*format*");
        new Func<object>(() => new Query<IFormattable, string>("decl-interface-query"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*IFormattable*format*");
        new Func<object>(() => new Source<Enum>("decl-abstract-source"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*Enum*format*");
    }

    [Fact]
    public void Without_a_format_DateTime_DateTimeOffset_and_TimeOnly_keys_are_rejected()
    {
        new Func<object>(() => new Source<DateTime>("decl-datetime"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*DateTime*format*");
        new Func<object>(() => new Source<DateTimeOffset>("decl-datetimeoffset"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*DateTimeOffset*format*");
        new Func<object>(() => new Query<TimeOnly, string>("decl-timeonly"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*TimeOnly*format*");
    }

    [Fact]
    public void Without_a_format_value_types_that_print_every_key_differently_are_accepted()
    {
        var declare = () =>
        {
            _ = new Source<Guid>("decl-guid");
            _ = new Source<DayOfWeek>("decl-enum");
            _ = new Source<DateOnly>("decl-dateonly");
            _ = new Query<Sku, string>("decl-user-formattable");
        };

        declare.Should().NotThrow();
    }

    [Fact]
    public void With_a_format_the_rejected_key_types_are_accepted()
    {
        var declare = () =>
        {
            _ = new Source<CustomerId>("decl-formatted-record", id => id.Value);
            _ = new Query<CustomerId, string>("decl-formatted-record", id => id.Value);
            _ = new Source<DateTime>(
                "decl-formatted-datetime",
                time => time.ToString("O", CultureInfo.InvariantCulture)
            );
            _ = new Source<DateTimeOffset>(
                "decl-formatted-datetimeoffset",
                time => time.ToString("O", CultureInfo.InvariantCulture)
            );
            _ = new Query<TimeOnly, string>(
                "decl-formatted-timeonly",
                time => time.ToString("O", CultureInfo.InvariantCulture)
            );
            _ = new Source<IFormattable>(
                "decl-formatted-interface",
                key => $"{key.GetType().Name}:{key.ToString(null, CultureInfo.InvariantCulture)}"
            );
        };

        declare.Should().NotThrow();
    }

    [Fact]
    public async Task A_null_key_throws()
    {
        await using var node = new SluiceNode();
        var customers = new Source<CustomerId>("decl-customers", id => id.Value);
        var statement = new Query<CustomerId, string>("decl-statement", id => id.Value);

        var record = () => customers.For(null!);
        var get = () =>
            node.Cache.GetOrSetAsync(statement, null!, (_, _) => Task.FromResult("never")).AsTask();

        record.Should().Throw<ArgumentNullException>();
        await get.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void A_format_that_returns_null_throws()
    {
        var customers = new Source<CustomerId>("decl-null-format", _ => null!);

        var record = () => customers.For(new CustomerId("c_1"));

        record.Should().Throw<InvalidOperationException>().WithMessage("*returned null*");
    }

    [Fact]
    public void Redeclaring_a_query_name_with_a_different_key_or_value_type_throws()
    {
        _ = new Query<int, string>("decl-redeclared-query");

        var otherKey = () => new Query<string, string>("decl-redeclared-query");
        var otherValue = () => new Query<int, int>("decl-redeclared-query");

        otherKey
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*already declared as Query<System.Int32, System.String>*");
        otherValue
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*already declared as Query<System.Int32, System.String>*");
    }

    [Fact]
    public void A_redeclaration_names_types_by_namespace_so_same_named_types_are_told_apart()
    {
        _ = new Query<CustomerId, string>("decl-same-named-types", id => id.Value);

        var other = () =>
            new Query<Other.CustomerId, string>("decl-same-named-types", id => id.Value);

        other
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "*Query<Sluice.Tests.DeclarationTests+CustomerId, System.String>*"
                    + "Query<Sluice.Tests.DeclarationTests+Other+CustomerId, System.String>*"
            );
    }

    [Fact]
    public void Redeclaring_a_query_name_with_the_same_types_is_allowed()
    {
        _ = new Query<int, string>("decl-repeated");

        var query = () => new Query<int, string>("decl-repeated", id => $"#{id}");

        query.Should().NotThrow();
    }

    // A source name clash only over-invalidates, so it isn't guarded.
    [Fact]
    public void Source_names_are_not_checked_for_duplicates()
    {
        _ = new Source<int>("decl-shared-source");

        var otherKey = () => new Source<string>("decl-shared-source");

        otherKey.Should().NotThrow();
    }
}
