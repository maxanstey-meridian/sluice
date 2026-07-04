namespace Sluice.Tests;

public sealed class ResourceAddressTests
{
    public sealed class Equality
    {
        [Fact]
        public void Same_Kind_Name_Key_Are_Equal()
        {
            var a = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            var b = new ResourceAddress(ResourceKind.Entity, "customer", "123");

            a.Should().Be(b);
        }

        [Fact]
        public void Different_Key_Are_Not_Equal()
        {
            var a = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            var b = new ResourceAddress(ResourceKind.Entity, "customer", "456");

            a.Should().NotBe(b);
        }

        [Fact]
        public void Different_Name_Are_Not_Equal()
        {
            var a = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            var b = new ResourceAddress(ResourceKind.Entity, "customers", "123");

            a.Should().NotBe(b);
        }

        [Fact]
        public void Different_Kind_Are_Not_Equal()
        {
            var a = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            var b = new ResourceAddress(ResourceKind.Collection, "customer", "123");

            a.Should().NotBe(b);
        }

        [Fact]
        public void Equal_Addresses_Have_Equal_HashCodes()
        {
            var a = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            var b = new ResourceAddress(ResourceKind.Entity, "customer", "123");

            a.GetHashCode().Should().Be(b.GetHashCode());
        }

        [Fact]
        public void All_Kinds_Support_Value_Equality()
        {
            var entityA = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            var entityB = new ResourceAddress(ResourceKind.Entity, "customer", "123");
            entityA.Should().Be(entityB);

            var collA = new ResourceAddress(ResourceKind.Collection, "orders", "123");
            var collB = new ResourceAddress(ResourceKind.Collection, "orders", "123");
            collA.Should().Be(collB);

            var extA = new ResourceAddress(ResourceKind.External, "stripe", "cus_123");
            var extB = new ResourceAddress(ResourceKind.External, "stripe", "cus_123");
            extA.Should().Be(extB);
        }
    }

    public sealed class Validation
    {
        [Fact]
        public void Constructor_Rejects_Colon_In_Name()
        {
            var act = () => new ResourceAddress(ResourceKind.Entity, "user:detail", "alice");
            act.Should().Throw<ArgumentException>()
               .WithMessage("*name*");
        }

        [Fact]
        public void Constructor_Rejects_Colon_In_Key()
        {
            var act = () => new ResourceAddress(ResourceKind.Entity, "user", "tenant:alice");
            act.Should().Throw<ArgumentException>()
               .WithMessage("*key*");
        }

        [Fact]
        public void Constructor_Accepts_Star_As_Key()
        {
            var addr = new ResourceAddress(ResourceKind.Entity, "user", "*");
            addr.Key.Should().Be("*");
            addr.ToString().Should().Be("entity:user:*");
        }

        [Fact]
        public void Constructor_Accepts_Normal_Values()
        {
            var addr = new ResourceAddress(ResourceKind.Collection, "orders.byCustomer", "customer-123");
            addr.Kind.Should().Be(ResourceKind.Collection);
            addr.Name.Should().Be("orders.byCustomer");
            addr.Key.Should().Be("customer-123");
        }

        [Fact]
        public void ToString_Produces_Expected_Format()
        {
            var addr = new ResourceAddress(ResourceKind.External, "stripe", "price-list");
            addr.ToString().Should().Be("external:stripe:price-list");
        }
    }

    public sealed class ToStringRendering
    {
        [Fact]
        public void Entity_Renders_Lowercase()
        {
            var address = new ResourceAddress(ResourceKind.Entity, "customer", "123");

            address.ToString().Should().Be("entity:customer:123");
        }

        [Fact]
        public void Collection_Renders_Lowercase()
        {
            var address = new ResourceAddress(ResourceKind.Collection, "orders.byCustomer", "123");

            address.ToString().Should().Be("collection:orders.byCustomer:123");
        }

        [Fact]
        public void External_Renders_Lowercase()
        {
            var address = new ResourceAddress(ResourceKind.External, "stripe.customer", "cus_123");

            address.ToString().Should().Be("external:stripe.customer:cus_123");
        }

        [Fact]
        public void ToString_Is_Deterministic()
        {
            var address = new ResourceAddress(ResourceKind.Entity, "customer", "123");

            address.ToString().Should().Be("entity:customer:123");
            address.ToString().Should().Be("entity:customer:123");
        }
    }
}
