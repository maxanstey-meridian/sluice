namespace Sluice.Tests;

// Example domain types — consumer types, defined in the test project only.
public sealed record CustomerId(string Value) : IResourceKey
{
    public override string ToString() => Value;
}

public sealed record OrderId(string Value) : IResourceKey
{
    public override string ToString() => Value;
}

public sealed class ResourceFactoryTests
{
    [Fact]
    public void Entity_For_Returns_Correct_Address()
    {
        var customer = new CustomerId("123");
        var address = Resource.Entity<CustomerId>("customer").For(customer);

        address.Kind.Should().Be(ResourceKind.Entity);
        address.Name.Should().Be("customer");
        address.Key.Should().Be("123");
        address.ToString().Should().Be("entity:customer:123");
    }

    [Fact]
    public void Collection_For_Returns_Correct_Address()
    {
        var customer = new CustomerId("123");
        var address = Resource.Collection<CustomerId>("orders.byCustomer").For(customer);

        address.Kind.Should().Be(ResourceKind.Collection);
        address.Name.Should().Be("orders.byCustomer");
        address.Key.Should().Be("123");
        address.ToString().Should().Be("collection:orders.byCustomer:123");
    }

    [Fact]
    public void External_For_Returns_Correct_Address()
    {
        var address = Resource.External("stripe.customer").For("cus_123");

        address.Kind.Should().Be(ResourceKind.External);
        address.Name.Should().Be("stripe.customer");
        address.Key.Should().Be("cus_123");
        address.ToString().Should().Be("external:stripe.customer:cus_123");
    }

    [Fact]
    public void Entity_Produces_Same_Address_For_Same_Key()
    {
        var id = new CustomerId("123");
        var customerResource = Resource.Entity<CustomerId>("customer");

        var address1 = customerResource.For(id);
        var address2 = customerResource.For(id);

        address1.Should().Be(address2);
    }

    [Fact]
    public void Collection_Produces_Same_Address_For_Same_Key()
    {
        var id = new CustomerId("123");
        var ordersResource = Resource.Collection<CustomerId>("orders.byCustomer");

        var address1 = ordersResource.For(id);
        var address2 = ordersResource.For(id);

        address1.Should().Be(address2);
    }
}
