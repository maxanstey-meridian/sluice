namespace Sluice.Tests;

public sealed class WriteEffectTests
{
    public sealed class WriteEffect_Factory
    {
        [Fact]
        public void For_Returns_NonNull_Instance()
        {
            var effect = WriteEffect.For();

            effect.Should().NotBeNull();
        }

        [Fact]
        public void For_Returns_Fresh_Instance_Each_Time()
        {
            var a = WriteEffect.For();
            var b = WriteEffect.For();

            a.Should().NotBe(b);
        }

        [Fact]
        public void New_Instance_Has_No_Addresses()
        {
            var effect = WriteEffect.For();

            effect.Resolve().Should().BeEmpty();
        }
    }

    public sealed class WriteEffect_Changes
    {
        [Fact]
        public void Changes_Accumulates_Addresses()
        {
            var effect = WriteEffect.For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "a", "1"))
                .Changes(new ResourceAddress(ResourceKind.Entity, "b", "2"))
                .Changes(new ResourceAddress(ResourceKind.Entity, "c", "3"));

            var resolved = effect.Resolve().ToList();

            resolved.Should().HaveCount(3);
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
            resolved[1].Should().Be(new ResourceAddress(ResourceKind.Entity, "b", "2"));
            resolved[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "c", "3"));
        }

        [Fact]
        public void Changes_Returns_Self_For_Chaining()
        {
            var effect = WriteEffect.For();
            var result = effect.Changes(new ResourceAddress(ResourceKind.Entity, "a", "1"));

            result.Should().Be(effect);
        }

        [Fact]
        public void Changes_Preserves_Order()
        {
            var effect = WriteEffect.For();

            effect.Changes(new ResourceAddress(ResourceKind.Entity, "first", "1"));
            effect.Changes(new ResourceAddress(ResourceKind.Entity, "second", "2"));
            effect.Changes(new ResourceAddress(ResourceKind.Entity, "third", "3"));

            var resolved = effect.Resolve().ToList();
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "first", "1"));
            resolved[1].Should().Be(new ResourceAddress(ResourceKind.Entity, "second", "2"));
            resolved[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "third", "3"));
        }
    }

    public sealed class WriteEffect_Generic
    {
        [Fact]
        public void For_Returns_NonNull_Instance()
        {
            var effect = WriteEffect<string>.For();

            effect.Should().NotBeNull();
        }

        [Fact]
        public void For_Returns_Fresh_Instance_Each_Time()
        {
            var a = WriteEffect<string>.For();
            var b = WriteEffect<string>.For();

            a.Should().NotBe(b);
        }
    }

    public sealed class WriteEffect_Generic_Changes
    {
        [Fact]
        public void Changes_Accumulates_Static_Addresses()
        {
            var effect = WriteEffect<int>.For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "a", "1"))
                .Changes(new ResourceAddress(ResourceKind.Collection, "b", "2"));

            var resolved = effect.Resolve(42).ToList();

            resolved.Should().HaveCount(2);
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
            resolved[1].Should().Be(new ResourceAddress(ResourceKind.Collection, "b", "2"));
        }

        [Fact]
        public void Changes_Returns_Self_For_Chaining()
        {
            var effect = WriteEffect<int>.For();
            var result = effect.Changes(new ResourceAddress(ResourceKind.Entity, "a", "1"));

            result.Should().Be(effect);
        }
    }

    public sealed class WriteEffect_Generic_ChangesResult
    {
        [Fact]
        public void ChangesResult_Accumulates_Resolvers()
        {
            var effect = WriteEffect<string>.For()
                .ChangesResult(r => new ResourceAddress(ResourceKind.Entity, "created", r));

            var resolved = effect.Resolve("myId").ToList();

            resolved.Should().HaveCount(1);
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "created", "myId"));
        }

        [Fact]
        public void ChangesResult_Returns_Self_For_Chaining()
        {
            var effect = WriteEffect<string>.For();
            var result = effect.ChangesResult(_ => new ResourceAddress(ResourceKind.Entity, "x", "0"));

            result.Should().Be(effect);
        }

        [Fact]
        public void Resolve_Returns_Static_Then_Result_Addresses()
        {
            var effect = WriteEffect<string>.For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "static1", "1"))
                .Changes(new ResourceAddress(ResourceKind.Collection, "static2", "2"))
                .ChangesResult(r => new ResourceAddress(ResourceKind.Entity, "dynamic", r))
                .ChangesResult(r => new ResourceAddress(ResourceKind.External, "ext", r));

            var resolved = effect.Resolve("val").ToList();

            resolved.Should().HaveCount(4);
            resolved[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "static1", "1"));
            resolved[1].Should().Be(new ResourceAddress(ResourceKind.Collection, "static2", "2"));
            resolved[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "dynamic", "val"));
            resolved[3].Should().Be(new ResourceAddress(ResourceKind.External, "ext", "val"));
        }

        [Fact]
        public void Resolve_With_No_Addresses_Returns_Empty()
        {
            var effect = WriteEffect<int>.For();

            effect.Resolve(0).Should().BeEmpty();
        }
    }

    public sealed class TypeSafety
    {
        [Fact]
        public void NonGeneric_WriteEffect_Has_No_ChangesResult_Method()
        {
            // Compile-time check: WriteEffect (non-generic) only has .Changes(), not .ChangesResult().
            WriteEffect effect = WriteEffect.For();
            effect.Changes(new ResourceAddress(ResourceKind.Entity, "x", "0"));
            // effect.ChangesResult(...) would not compile — compiler error, no assertion needed.
        }

        [Fact]
        public void Generic_WriteEffect_Has_Changes_And_ChangesResult()
        {
            WriteEffect<string> effect = WriteEffect<string>.For();
            effect.Changes(new ResourceAddress(ResourceKind.Entity, "x", "0"));
            effect.ChangesResult(_ => new ResourceAddress(ResourceKind.Entity, "y", ""));
        }
    }
}
