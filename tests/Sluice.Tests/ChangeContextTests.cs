namespace Sluice.Tests;

public sealed class ChangeContextTests
{
    public sealed class HighLevelApply
    {
        [Fact]
        public async Task Void_Form_Executes_Work_And_Resolves_Addresses()
        {
            var ctx = new ChangeContext(CancellationToken.None);
            var effect = new WriteEffect(
                new ResourceAddress(ResourceKind.Entity, "entity", "1"),
                new ResourceAddress(ResourceKind.Collection, "coll", "2")
            );

            await ctx.Apply(
                async () =>
                {
                    await Task.Delay(0);
                },
                effect
            );

            ctx.ChangedAddresses.Should().HaveCount(2);
            ctx.ChangedAddresses[0]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Entity, "entity", "1"));
            ctx.ChangedAddresses[1]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Collection, "coll", "2"));
        }

        [Fact]
        public async Task Typed_Form_Executes_Work_Returns_Result_And_Resolves_Addresses()
        {
            var ctx = new ChangeContext(CancellationToken.None);
            var effect = new WriteEffect<string>(
                new ResourceAddress(ResourceKind.Entity, "entity", "1")
            ).ChangesResult(result => new ResourceAddress(ResourceKind.Entity, "result", result));

            var result = await ctx.Apply(
                async () =>
                {
                    await Task.Delay(0);
                    return "myResult";
                },
                effect
            );

            result.Should().Be("myResult");
            ctx.ChangedAddresses.Should().HaveCount(2);
            ctx.ChangedAddresses[0]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Entity, "entity", "1"));
            ctx.ChangedAddresses[1]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Entity, "result", "myResult"));
        }

        [Fact]
        public async Task Void_Form_Throws_Nothing_If_Work_Throws()
        {
            var ctx = new ChangeContext(CancellationToken.None);
            var effect = new WriteEffect(new ResourceAddress(ResourceKind.Entity, "entity", "1"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ctx.Apply(() => throw new InvalidOperationException("boom"), effect)
            );
            Assert.Equal("boom", ex.Message);

            ctx.ChangedAddresses.Should().BeEmpty();
        }

        [Fact]
        public async Task Typed_Form_Throws_Nothing_If_Work_Throws()
        {
            var ctx = new ChangeContext(CancellationToken.None);
            var effect = new WriteEffect<int>(
                new ResourceAddress(ResourceKind.Entity, "entity", "1")
            );

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ctx.Apply(
                    async () =>
                    {
                        await Task.Delay(0);
                        throw new InvalidOperationException("boom");
                    },
                    effect
                )
            );
            Assert.Equal("boom", ex.Message);

            ctx.ChangedAddresses.Should().BeEmpty();
        }
    }

    [Fact]
    public void CancellationToken_Flows_Through_Constructor()
    {
        var ct = new CancellationToken(canceled: true);
        var ctx = new ChangeContext(ct);

        ctx.CancellationToken.Should().Be(ct);
    }
}
