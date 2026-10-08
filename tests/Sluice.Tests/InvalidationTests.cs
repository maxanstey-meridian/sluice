using Sluice.Tests.Support;

namespace Sluice.Tests;

public sealed class InvalidationTests
{
    private static readonly Source<int> _invoices = new("invalidation-invoices");
    private static readonly Source<int> _orders = new("invalidation-orders");
    private static readonly Source<int> _payments = new("invalidation-payments");
    private static readonly Query<string, string> _summary = new("invalidation-summary");

    [Fact]
    public async Task Invalidating_one_key_evicts_readers_of_that_key_and_of_the_whole_source_but_not_of_other_keys()
    {
        await using var node = new SluiceNode();
        var reader = new CountingReader(node.Cache, _summary);
        await reader.GetAsync("one", _invoices.For(1));
        await reader.GetAsync("other", _invoices.For(2));
        await reader.GetAsync("all", _invoices.All);

        await node.Cache.InvalidateAsync(_invoices.For(1));
        await reader.GetAsync("one", _invoices.For(1));
        await reader.GetAsync("other", _invoices.For(2));
        await reader.GetAsync("all", _invoices.All);

        reader.Runs("one").Should().Be(2);
        reader.Runs("all").Should().Be(2);
        reader.Runs("other").Should().Be(1);
    }

    [Fact]
    public async Task Invalidating_the_whole_source_evicts_every_reader_of_anything_in_it()
    {
        await using var node = new SluiceNode();
        var reader = new CountingReader(node.Cache, _summary);
        await reader.GetAsync("one", _invoices.For(1));
        await reader.GetAsync("other", _invoices.For(2));
        await reader.GetAsync("all", _invoices.All);
        await reader.GetAsync("unrelated", _orders.All);

        await node.Cache.InvalidateAsync(_invoices.All);
        await reader.GetAsync("one", _invoices.For(1));
        await reader.GetAsync("other", _invoices.For(2));
        await reader.GetAsync("all", _invoices.All);
        await reader.GetAsync("unrelated", _orders.All);

        reader.Runs("one").Should().Be(2);
        reader.Runs("other").Should().Be(2);
        reader.Runs("all").Should().Be(2);
        reader.Runs("unrelated").Should().Be(1);
    }

    [Fact]
    public async Task A_batch_invalidation_evicts_the_union()
    {
        await using var node = new SluiceNode();
        var reader = new CountingReader(node.Cache, _summary);
        await reader.GetAsync("invoice", _invoices.For(1));
        await reader.GetAsync("order", _orders.For(2));
        await reader.GetAsync("payment", _payments.For(3));

        await node.Cache.InvalidateAsync([_invoices.For(1), _orders.For(2)]);
        await reader.GetAsync("invoice", _invoices.For(1));
        await reader.GetAsync("order", _orders.For(2));
        await reader.GetAsync("payment", _payments.For(3));

        reader.Runs("invoice").Should().Be(2);
        reader.Runs("order").Should().Be(2);
        reader.Runs("payment").Should().Be(1);
    }

    [Fact]
    public async Task Sync_Invalidate_evicts_like_InvalidateAsync()
    {
        await using var node = new SluiceNode();
        var reader = new CountingReader(node.Cache, _summary);
        await reader.GetAsync("invoice", _invoices.For(1));
        await reader.GetAsync("order", _orders.For(2));
        await reader.GetAsync("all-payments", _payments.All);
        await reader.GetAsync("untouched", _payments.For(9));

        node.Cache.Invalidate(_invoices.For(1));
        node.Cache.Invalidate([_orders.For(2), _payments.For(3)]);
        await reader.GetAsync("invoice", _invoices.For(1));
        await reader.GetAsync("order", _orders.For(2));
        await reader.GetAsync("all-payments", _payments.All);
        await reader.GetAsync("untouched", _payments.For(9));

        reader.Runs("invoice").Should().Be(2);
        reader.Runs("order").Should().Be(2);
        reader.Runs("all-payments").Should().Be(2);
        reader.Runs("untouched").Should().Be(1);
    }

    [Fact]
    public async Task A_null_dependency_throws()
    {
        await using var node = new SluiceNode();

        var single = () => node.Cache.InvalidateAsync((Dependency)null!).AsTask();
        var inList = () => node.Cache.InvalidateAsync([_invoices.For(1), null!]).AsTask();
        var syncInList = () => node.Cache.Invalidate([_invoices.For(1), null!]);

        await single.Should().ThrowExactlyAsync<ArgumentNullException>();
        await inList.Should().ThrowExactlyAsync<ArgumentException>().WithMessage("*null*");
        syncInList.Should().ThrowExactly<ArgumentException>().WithMessage("*null*");
    }

    [Fact]
    public async Task ClearAsync_evicts_everything()
    {
        await using var node = new SluiceNode();
        var reader = new CountingReader(node.Cache, _summary);
        await reader.GetAsync("invoice", _invoices.For(1));
        await reader.GetAsync("order", _orders.All);

        await node.Cache.ClearAsync();
        await reader.GetAsync("invoice", _invoices.For(1));
        await reader.GetAsync("order", _orders.All);

        reader.Runs("invoice").Should().Be(2);
        reader.Runs("order").Should().Be(2);
    }
}
