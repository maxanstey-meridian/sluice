using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sluice.Tests;

public sealed class SerializationTests
{
    private sealed record NestedItem(Guid Id, string Label, DateTimeOffset CreatedAt);

    private sealed record ComplexDto(
        Guid Id,
        string Name,
        DateTimeOffset Timestamp,
        NestedItem Primary,
        List<NestedItem> Items
    );

    [Fact]
    public void Default_Options_RoundTrips_CacheEntry_With_ComplexDto()
    {
        var primary = new NestedItem(
            Guid.NewGuid(),
            "primary-item",
            new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero)
        );

        var items = new List<NestedItem>
        {
            new(Guid.NewGuid(), "item-a", DateTimeOffset.Parse("2025-01-01T00:00:00+00:00")),
            new(Guid.NewGuid(), "item-b", DateTimeOffset.Parse("2025-02-01T00:00:00+00:00")),
        };

        var dto = new ComplexDto(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "test-dto",
            DateTimeOffset.Parse("2025-03-01T12:00:00+00:00"),
            primary,
            items
        );

        var observedReads = new List<ResourceAddress>
        {
            new(ResourceKind.Entity, "user", "alice"),
            new(ResourceKind.Collection, "orders", "customer-1"),
            new(ResourceKind.External, "stripe", "cus_abc"),
        };

        var entry = new CacheEntry<ComplexDto>(
            dto,
            observedReads,
            DateTimeOffset.Parse("2025-03-01T12:00:00+00:00"),
            DateTimeOffset.Parse("2025-03-02T12:00:00+00:00")
        );

        var json = JsonSerializer.Serialize(entry);
        var result = JsonSerializer.Deserialize<CacheEntry<ComplexDto>>(json);

        result.Should().NotBeNull();
        result!.Value.Id.Should().Be(dto.Id);
        result.Value.Name.Should().Be(dto.Name);
        result.Value.Timestamp.Should().Be(dto.Timestamp);
        result.Value.Primary.Id.Should().Be(primary.Id);
        result.Value.Primary.Label.Should().Be(primary.Label);
        result.Value.Primary.CreatedAt.Should().Be(primary.CreatedAt);
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Label.Should().Be("item-a");
        result.Value.Items[1].Label.Should().Be("item-b");
        result.ObservedReads.Should().HaveCount(3);
        result.ObservedReads[0].Kind.Should().Be(ResourceKind.Entity);
        result.ObservedReads[0].Name.Should().Be("user");
        result.ObservedReads[0].Key.Should().Be("alice");
        result.ObservedReads[1].Kind.Should().Be(ResourceKind.Collection);
        result.ObservedReads[1].Name.Should().Be("orders");
        result.ObservedReads[1].Key.Should().Be("customer-1");
        result.ObservedReads[2].Kind.Should().Be(ResourceKind.External);
        result.ObservedReads[2].Name.Should().Be("stripe");
        result.ObservedReads[2].Key.Should().Be("cus_abc");
        result.CachedAt.Should().Be(entry.CachedAt);
        result.ExpiresAt.Should().Be(entry.ExpiresAt);
    }

    [Fact]
    public void Custom_Options_CamelCase_EnumString_RoundTrips_CacheEntry_With_ComplexDto()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        var primary = new NestedItem(
            Guid.NewGuid(),
            "primary-item",
            new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero)
        );

        var items = new List<NestedItem>
        {
            new(Guid.NewGuid(), "item-a", DateTimeOffset.Parse("2025-01-01T00:00:00+00:00")),
            new(Guid.NewGuid(), "item-b", DateTimeOffset.Parse("2025-02-01T00:00:00+00:00")),
        };

        var dto = new ComplexDto(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "test-dto",
            DateTimeOffset.Parse("2025-03-01T12:00:00+00:00"),
            primary,
            items
        );

        var observedReads = new List<ResourceAddress>
        {
            new(ResourceKind.Entity, "user", "alice"),
            new(ResourceKind.Collection, "orders", "customer-1"),
            new(ResourceKind.External, "stripe", "cus_abc"),
        };

        var entry = new CacheEntry<ComplexDto>(
            dto,
            observedReads,
            DateTimeOffset.Parse("2025-03-01T12:00:00+00:00"),
            DateTimeOffset.Parse("2025-03-02T12:00:00+00:00")
        );

        var json = JsonSerializer.Serialize(entry, options);
        var result = JsonSerializer.Deserialize<CacheEntry<ComplexDto>>(json, options);

        result.Should().NotBeNull();
        result!.Value.Id.Should().Be(dto.Id);
        result.Value.Name.Should().Be(dto.Name);
        result.Value.Timestamp.Should().Be(dto.Timestamp);
        result.Value.Primary.Id.Should().Be(primary.Id);
        result.Value.Primary.Label.Should().Be(primary.Label);
        result.Value.Primary.CreatedAt.Should().Be(primary.CreatedAt);
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Label.Should().Be("item-a");
        result.Value.Items[1].Label.Should().Be("item-b");
        result.ObservedReads.Should().HaveCount(3);
        result.ObservedReads[0].Kind.Should().Be(ResourceKind.Entity);
        result.ObservedReads[0].Name.Should().Be("user");
        result.ObservedReads[0].Key.Should().Be("alice");
        result.ObservedReads[1].Kind.Should().Be(ResourceKind.Collection);
        result.ObservedReads[1].Name.Should().Be("orders");
        result.ObservedReads[1].Key.Should().Be("customer-1");
        result.ObservedReads[2].Kind.Should().Be(ResourceKind.External);
        result.ObservedReads[2].Name.Should().Be("stripe");
        result.ObservedReads[2].Key.Should().Be("cus_abc");
        result.CachedAt.Should().Be(entry.CachedAt);
        result.ExpiresAt.Should().Be(entry.ExpiresAt);
    }
}
