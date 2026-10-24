using System.Globalization;
using System.Linq.Expressions;
using Sluice.Infrastructure;
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

    // One lambda for every T: the same format whichever generic type holds it.
    private static class Holder<T>
    {
        public static Query<int, string> Query(string name) => new(name, id => typeof(T).Name + id);

        public static Source<int> Source(string name) => new(name, id => typeof(T).Name + id);
    }

    private static string Hash(int id) => $"#{id}";

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
    public void Redeclaring_a_query_or_source_name_with_the_same_types_and_format_is_allowed()
    {
        _ = new Query<int, string>("decl-repeated");
        _ = new Query<int, string>("decl-repeated-format", Hash);
        _ = new Source<int>("decl-repeated");
        _ = new Source<int>("decl-repeated-format", Hash);

        var redeclare = () =>
        {
            _ = new Query<int, string>("decl-repeated");
            _ = new Query<int, string>("decl-repeated-format", Hash);
            _ = new Source<int>("decl-repeated");
            _ = new Source<int>("decl-repeated-format", Hash);
        };

        redeclare.Should().NotThrow();
    }

    // Two lambdas written the same way are still two formats: Sluice can't tell they agree.
    [Fact]
    public void Redeclaring_a_query_or_source_name_with_another_format_throws()
    {
        _ = new Query<int, string>("decl-default-then-format");
        _ = new Query<int, string>("decl-two-lambdas", id => $"#{id}");
        _ = new Source<int>("decl-default-then-format");
        _ = new Source<int>("decl-two-lambdas", id => $"#{id}");

        new Func<object>(() => new Query<int, string>("decl-default-then-format", Hash))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "'decl-default-then-format' is already declared as Query<System.Int32, System.String> with another "
                    + "format (the default format, with no format given), so one key could get different text in each, "
                    + "splitting or mixing their entries. Declare the query once and use that declaration everywhere.*"
            );
        new Func<object>(() => new Query<int, string>("decl-two-lambdas", id => $"#{id}"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*with another format (Sluice.Tests.DeclarationTests+<>c.*)*");
        new Func<object>(() => new Source<int>("decl-default-then-format", Hash))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Source 'decl-default-then-format' is already declared as Source<System.Int32> with another format "
                    + "(the default format, with no format given), so one key could get different text in each, and a "
                    + "write through one would miss the readers of the other.*"
            );
        new Func<object>(() => new Source<int>("decl-two-lambdas", id => $"#{id}"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*with another format (Sluice.Tests.DeclarationTests+<>c.*)*");
    }

    [Fact]
    public void Redeclaring_a_query_or_source_name_from_one_lambda_with_different_captures_or_generic_holders_is_allowed()
    {
        static Query<int, string> DeclareQuery(string prefix) =>
            new("decl-captures", id => prefix + id);
        static Source<int> DeclareSource(string prefix) => new("decl-captures", id => prefix + id);

        var redeclare = () =>
        {
            _ = DeclareQuery("a");
            _ = DeclareQuery("b");
            _ = DeclareSource("a");
            _ = DeclareSource("b");
            _ = Holder<string>.Query("decl-generic-holders");
            _ = Holder<Uri>.Query("decl-generic-holders");
            _ = Holder<string>.Source("decl-generic-holders");
            _ = Holder<Uri>.Source("decl-generic-holders");
        };

        redeclare.Should().NotThrow();
    }

    // A compiled expression is a new method on every compile, so it can't be compared; the first format that can be is
    // what later declarations must match.
    [Fact]
    public void A_query_or_source_format_from_a_compiled_expression_is_not_compared()
    {
        Expression<Func<int, string>> format = id => id.ToString(CultureInfo.InvariantCulture);
        _ = new Query<int, string>("decl-compiled", format.Compile());
        _ = new Source<int>("decl-compiled", format.Compile());

        var redeclare = () =>
        {
            _ = new Query<int, string>("decl-compiled", format.Compile());
            _ = new Query<int, string>("decl-compiled", Hash);
            _ = new Source<int>("decl-compiled", format.Compile());
            _ = new Source<int>("decl-compiled", Hash);
        };

        redeclare.Should().NotThrow();
        new Func<object>(() => new Query<int, string>("decl-compiled"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*with another format (Sluice.Tests.DeclarationTests.Hash)*");
        new Func<object>(() => new Source<int>("decl-compiled"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*with another format (Sluice.Tests.DeclarationTests.Hash)*");
    }

    // Int 1 and string "01" are one key to one declaration's readers and another to the other's writers.
    [Fact]
    public void Redeclaring_a_source_name_with_another_key_type_throws()
    {
        _ = new Source<int>("decl-shared-source");

        var otherKey = () => new Source<string>("decl-shared-source");

        otherKey
            .Should()
            .Throw<ArgumentException>()
            .WithMessage(
                "Source 'decl-shared-source' is already declared as Source<System.Int32>, so declaring it as "
                    + "Source<System.String> would give one key different text in each*"
            );
    }

    // A package's model names are unique by construction, and two models over one table share them on purpose.
    [Fact]
    public void A_model_source_is_not_checked_against_other_declarations_and_claims_nothing()
    {
        _ = new Source<int>("decl-model");

        var model = () =>
        {
            _ = ModelSource.Create<int>("decl-model", id => $"#{id}");
            _ = ModelSource.Create<string>("decl-model", id => id);
            _ = ModelSource.Create<object>("decl-model-only", id => $"{id}");
        };

        model.Should().NotThrow();
        new Func<object>(() => new Source<string>("decl-model-only")).Should().NotThrow();
        new Func<object>(() => ModelSource.Create<int>(" ", id => $"{id}"))
            .Should()
            .Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("statement:v2")]
    [InlineData("__fc")]
    public void A_keyless_query_name_is_checked_as_a_keyed_one_is(string name)
    {
        var declare = () => new Query<string>(name);

        declare.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_keyless_and_a_keyed_query_cannot_share_a_name()
    {
        _ = new Query<int, string>("decl-keyed-and-keyless");

        var keyless = () => new Query<string>("decl-keyed-and-keyless");

        keyless
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*already declared as Query<System.Int32, System.String>*");
    }
}
