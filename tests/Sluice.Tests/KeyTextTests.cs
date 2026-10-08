using System.Globalization;
using Sluice.Tests.Support;

namespace Sluice.Tests;

public sealed class KeyTextTests
{
    public sealed record CustomerId(string Value);

    private static readonly Source<int> _rows = new("keytext-rows");
    private static readonly Query<decimal, int> _byAmount = new("keytext-by-amount");
    private static readonly Query<double, int> _byRatio = new("keytext-by-ratio");

    // Customer ids are case-insensitive here, so the format folds case.
    private static readonly Query<CustomerId, string> _byCustomer = new(
        "keytext-by-customer",
        id => id.Value.ToLowerInvariant()
    );

    /// <summary>
    /// Caches <paramref name="key"/> once under the invariant culture and once under <c>de-DE</c>, and returns
    /// how many computes ran, i.e. how many distinct entries the two calls got.
    /// </summary>
    private static async Task<int> EntriesAcrossCultures<TKey>(Query<TKey, int> query, TKey key)
        where TKey : notnull
    {
        await using var node = new SluiceNode();
        var runs = 0;

        async Task Get(CultureInfo culture)
        {
            // CurrentCulture flows with the async context, so this changes only this test's flow.
            CultureInfo.CurrentCulture = culture;
            await node.Cache.GetOrSetAsync(
                query,
                key,
                (read, _) =>
                {
                    read.From(_rows.For(1));
                    return Task.FromResult(++runs);
                }
            );
        }

        await Get(CultureInfo.InvariantCulture);
        await Get(CultureInfo.GetCultureInfo("de-DE"));
        return runs;
    }

    // T20
    [Fact]
    public async Task A_decimal_key_gets_the_same_entry_under_a_German_culture()
    {
        (await EntriesAcrossCultures(_byAmount, 1.5m)).Should().Be(1);
    }

    // T20
    [Fact]
    public async Task A_double_key_gets_the_same_entry_under_a_German_culture()
    {
        (await EntriesAcrossCultures(_byRatio, 1.5)).Should().Be(1);
    }

    // T20
    [Fact]
    public async Task A_format_is_used_as_given()
    {
        await using var node = new SluiceNode();
        var runs = 0;

        Task<string> Statement(SluiceReads read, CancellationToken ct)
        {
            runs++;
            read.From(_rows.For(1));
            return Task.FromResult("statement");
        }

        await node.Cache.GetOrSetAsync(_byCustomer, new CustomerId("C_42"), Statement);
        await node.Cache.GetOrSetAsync(_byCustomer, new CustomerId("c_42"), Statement);

        runs.Should().Be(1, "the format gives both keys the same text, so they share an entry");
    }
}
