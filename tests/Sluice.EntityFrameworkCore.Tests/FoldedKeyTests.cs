using Microsoft.EntityFrameworkCore;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FoldedKeyTests(PostgresFixture postgres)
{
    private static readonly Query<decimal, Price> _price = new("ef-folded-price");
    private static readonly Query<string, Tag> _tag = new("ef-folded-tag");
    private static readonly Query<string, Product> _product = new("ef-folded-product");

    [Fact]
    public async Task A_numeric_key_read_as_1_is_evicted_by_the_same_value_written_as_1_00()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Prices.Add(new Price { Amount = 1.00m, Label = "one" });
        await db.SaveChangesAsync();

        Task<Price> ReadOne(SluiceReads read, CancellationToken ct) =>
            read.Entity(db.Prices, 1m).SingleAsync(ct);

        (await node.Cache.GetOrSetAsync(_price, 1m, ReadOne)).Label.Should().Be("one");
        (await CachedRead.IsEvictedBy(ReadOne, db.Prices.Dependency(1.00m))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadOne, db.Prices.Dependency(1.01m))).Should().BeFalse();
    }

    [Fact]
    public async Task A_citext_key_read_in_one_case_is_evicted_by_the_same_key_in_another()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Tags.Add(new Tag { Name = "Widget" });
        await db.SaveChangesAsync();

        Task<Tag> ReadWidget(SluiceReads read, CancellationToken ct) =>
            read.Entity(db.Tags, "WIDGET").SingleAsync(ct);

        (await node.Cache.GetOrSetAsync(_tag, "WIDGET", ReadWidget)).Name.Should().Be("Widget");
        (await CachedRead.IsEvictedBy(ReadWidget, db.Tags.Dependency("widget"))).Should().BeTrue();
        (await CachedRead.IsEvictedBy(ReadWidget, db.Tags.Dependency("gadget"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_strongly_typed_key_folds_the_value_its_converter_stores()
    {
        await using var node = new SluiceNode();
        await using var db = postgres.CreateDb(node);
        db.Products.Add(new Product { Sku = new Sku("Tea-1"), Name = "Tea" });
        await db.SaveChangesAsync();

        Task<Product> ReadTea(SluiceReads read, CancellationToken ct) =>
            read.Entity(db.Products, new Sku("TEA-1")).SingleAsync(ct);

        (await node.Cache.GetOrSetAsync(_product, "TEA-1", ReadTea)).Name.Should().Be("Tea");
        (await CachedRead.IsEvictedBy(ReadTea, db.Products.Dependency(new Sku("tea-1"))))
            .Should()
            .BeTrue();
        (await CachedRead.IsEvictedBy(ReadTea, db.Products.Dependency(new Sku("tea-2"))))
            .Should()
            .BeFalse();
    }
}
