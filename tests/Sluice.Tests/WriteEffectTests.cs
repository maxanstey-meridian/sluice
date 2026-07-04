namespace Sluice.Tests;

public sealed class WriteEffectTests
{
    public sealed class WriteEffect_Construction
    {
        [Fact]
        public void Empty_Construction_Has_No_Addresses()
        {
            var effect = new WriteEffect();

            effect.Addresses.Should().BeEmpty();
        }

        [Fact]
        public void Params_Construction_Preserves_Addresses()
        {
            var effect = new WriteEffect(
                new ResourceAddress(ResourceKind.Entity, "a", "1"),
                new ResourceAddress(ResourceKind.Entity, "b", "2"),
                new ResourceAddress(ResourceKind.Entity, "c", "3")
            );

            var addresses = effect.Addresses.ToList();

            addresses.Should().HaveCount(3);
            addresses[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
            addresses[1].Should().Be(new ResourceAddress(ResourceKind.Entity, "b", "2"));
            addresses[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "c", "3"));
        }

        [Fact]
        public void Each_Construction_Is_A_Fresh_Instance()
        {
            var a = new WriteEffect();
            var b = new WriteEffect();

            a.Should().NotBe(b);
        }
    }

    public sealed class WriteEffect_Generic_Construction
    {
        [Fact]
        public void Empty_Construction_Has_No_Static_Addresses()
        {
            var effect = new WriteEffect<int>();

            effect.Resolve(0).Should().BeEmpty();
        }

        [Fact]
        public void Params_Construction_Preserves_Static_Addresses()
        {
            var effect = new WriteEffect<int>(
                new ResourceAddress(ResourceKind.Entity, "a", "1"),
                new ResourceAddress(ResourceKind.Collection, "b", "2")
            );

            var resolved = effect.Resolve(42).ToList();

            resolved.Should().HaveCount(2);
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
            resolved[1].Should().Be(new ResourceAddress(ResourceKind.Collection, "b", "2"));
        }
    }

    public sealed class WriteEffect_Generic_ChangesResult
    {
        [Fact]
        public void ChangesResult_Accumulates_Resolvers()
        {
            var effect = new WriteEffect<string>().ChangesResult(
                r => new ResourceAddress(ResourceKind.Entity, "created", r)
            );

            var resolved = effect.Resolve("myId").ToList();

            resolved.Should().HaveCount(1);
            resolved[0]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Entity, "created", "myId"));
        }

        [Fact]
        public void ChangesResult_Returns_Self_For_Chaining()
        {
            var effect = new WriteEffect<string>();
            var result = effect.ChangesResult(_ =>
                new ResourceAddress(ResourceKind.Entity, "x", "0")
            );

            result.Should().Be(effect);
        }

        [Fact]
        public void Resolve_Returns_Static_Then_Result_Addresses()
        {
            var effect = new WriteEffect<string>(
                new ResourceAddress(ResourceKind.Entity, "static1", "1"),
                new ResourceAddress(ResourceKind.Collection, "static2", "2")
            )
                .ChangesResult(r => new ResourceAddress(ResourceKind.Entity, "dynamic", r))
                .ChangesResult(r => new ResourceAddress(ResourceKind.External, "ext", r));

            var resolved = effect.Resolve("val").ToList();

            resolved.Should().HaveCount(4);
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "static1", "1"));
            resolved[1].Should().Be(new ResourceAddress(ResourceKind.Collection, "static2", "2"));
            resolved[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "dynamic", "val"));
            resolved[3].Should().Be(new ResourceAddress(ResourceKind.External, "ext", "val"));
        }
    }

    public sealed class TypeSafety
    {
        [Fact]
        public void NonGeneric_WriteEffect_Has_No_ChangesResult_Method()
        {
            // Compile-time check: WriteEffect (non-generic) has no .ChangesResult().
            // Only WriteEffect<T> has it.
            WriteEffect effect = new(
                new ResourceAddress(ResourceKind.Entity, "x", "0")
            );
            // effect.ChangesResult(...) would not compile — compiler error, no assertion needed.
        }

        [Fact]
        public void Generic_WriteEffect_Has_ChangesResult()
        {
            WriteEffect<string> effect = new WriteEffect<string>().ChangesResult(_ =>
                new ResourceAddress(ResourceKind.Entity, "y", "")
            );
        }
    }
}
