using System.Globalization;
using Sluice.Tests.Support;

namespace Sluice.Tests;

public sealed class DeclarationTests
{
    public sealed record CustomerId(string Value);

    // T12
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Blank_source_names_are_rejected(string name)
    {
        var declare = () => new Source<int>(name);

        declare.Should().Throw<ArgumentException>();
    }

    // T12
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("statement:v2")]
    public void Blank_query_names_and_names_containing_a_colon_are_rejected(string name)
    {
        var declare = () => new Query<int, string>(name);

        declare.Should().Throw<ArgumentException>();
    }

    // T12
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

    // T12
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

    // T12
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
        };

        declare.Should().NotThrow();
    }

    // T12
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

    // T28
    [Fact]
    public void Redeclaring_a_query_name_with_a_different_key_or_value_type_throws()
    {
        _ = new Query<int, string>("decl-redeclared-query");

        var otherKey = () => new Query<string, string>("decl-redeclared-query");
        var otherValue = () => new Query<int, int>("decl-redeclared-query");

        otherKey
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*already declared as Query<Int32, String>*");
        otherValue
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*already declared as Query<Int32, String>*");
    }

    // T28
    [Fact]
    public void Redeclaring_a_query_name_with_the_same_types_is_allowed()
    {
        _ = new Query<int, string>("decl-repeated");

        var query = () => new Query<int, string>("decl-repeated", id => $"#{id}");

        query.Should().NotThrow();
    }

    // D13: a source name clash only over-invalidates, so it isn't guarded.
    [Fact]
    public void Source_names_are_not_checked_for_duplicates()
    {
        _ = new Source<int>("decl-shared-source");

        var otherKey = () => new Source<string>("decl-shared-source");

        otherKey.Should().NotThrow();
    }
}
