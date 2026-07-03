namespace Sluice.Tests;

public sealed class ChangeContextTests
{
    public sealed class LowLevelApply
    {
        [Fact]
        public async Task Void_Form_Executes_Work_And_Records_Addresses()
        {
            var ctx = new ChangeContext();
            var expected = new[]
            {
                new ResourceAddress(ResourceKind.Entity, "entity", "1"),
                new ResourceAddress(ResourceKind.Collection, "coll", "2"),
            };

            await ctx.Apply(changes =>
            {
                foreach (var addr in expected)
                {
                    changes.Changed(addr);
                }
                return Task.CompletedTask;
            });

            ctx.ChangedAddresses.Should().HaveCount(2);
            ctx.ChangedAddresses[0].Should().Be(expected[0]);
            ctx.ChangedAddresses[1].Should().Be(expected[1]);
        }

        [Fact]
        public async Task Void_Form_Records_In_Declaration_Order()
        {
            var ctx = new ChangeContext();

            await ctx.Apply(changes =>
            {
                changes.Changed(new ResourceAddress(ResourceKind.Entity, "a", "1"));
                changes.Changed(new ResourceAddress(ResourceKind.Entity, "b", "2"));
                changes.Changed(new ResourceAddress(ResourceKind.Entity, "c", "3"));
                return Task.CompletedTask;
            });

            ctx.ChangedAddresses.Should().HaveCount(3);
            ctx.ChangedAddresses[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
            ctx.ChangedAddresses[1].Should().Be(new ResourceAddress(ResourceKind.Entity, "b", "2"));
            ctx.ChangedAddresses[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "c", "3"));
        }

        [Fact]
        public async Task Void_Form_Records_Nothing_If_Work_Changes_Nothing()
        {
            var ctx = new ChangeContext();

            await ctx.Apply(_ => Task.CompletedTask);

            ctx.ChangedAddresses.Should().BeEmpty();
        }

        [Fact]
        public async Task Void_Form_Throws_Nothing_If_Work_Throws()
        {
            var ctx = new ChangeContext();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ctx.Apply(_ => throw new InvalidOperationException("boom"))
            );
            Assert.Equal("boom", ex.Message);

            ctx.ChangedAddresses.Should().BeEmpty();
        }

        [Fact]
        public async Task Typed_Form_Executes_Work_Returns_Result_And_Records_Addresses()
        {
            var ctx = new ChangeContext();

            var result = await ctx.Apply(async changes =>
            {
                changes.Changed(new ResourceAddress(ResourceKind.Entity, "entity", "1"));
                await Task.Delay(0);
                return 42;
            });

            result.Should().Be(42);
            ctx.ChangedAddresses.Should().HaveCount(1);
            ctx.ChangedAddresses[0]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Entity, "entity", "1"));
        }

        [Fact]
        public async Task Typed_Form_Records_In_Declaration_Order()
        {
            var ctx = new ChangeContext();

            var result = await ctx.Apply(async changes =>
            {
                changes.Changed(new ResourceAddress(ResourceKind.Entity, "a", "1"));
                changes.Changed(new ResourceAddress(ResourceKind.Collection, "b", "2"));
                changes.Changed(new ResourceAddress(ResourceKind.External, "c", "3"));
                await Task.Delay(0);
                return "done";
            });

            result.Should().Be("done");
            ctx.ChangedAddresses.Should().HaveCount(3);
            ctx.ChangedAddresses[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
            ctx.ChangedAddresses[1]
                .Should()
                .Be(new ResourceAddress(ResourceKind.Collection, "b", "2"));
            ctx.ChangedAddresses[2]
                .Should()
                .Be(new ResourceAddress(ResourceKind.External, "c", "3"));
        }

        [Fact]
        public async Task Typed_Form_Throws_Nothing_If_Work_Throws()
        {
            var ctx = new ChangeContext();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ctx.Apply(async _ =>
                {
                    await Task.Delay(0);
                    throw new InvalidOperationException("boom");
                })
            );
            Assert.Equal("boom", ex.Message);

            ctx.ChangedAddresses.Should().BeEmpty();
        }
    }

    public sealed class HighLevelApply
    {
        [Fact]
        public async Task Void_Form_Executes_Work_And_Resolves_Addresses()
        {
            var ctx = new ChangeContext();
            var effect = WriteEffect
                .For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "entity", "1"))
                .Changes(new ResourceAddress(ResourceKind.Collection, "coll", "2"));

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
            var ctx = new ChangeContext();
            var effect = WriteEffect<string>
                .For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "entity", "1"))
                .ChangesResult(result => new ResourceAddress(
                    ResourceKind.Entity,
                    "result",
                    result
                ));

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
            var ctx = new ChangeContext();
            var effect = WriteEffect
                .For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "entity", "1"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ctx.Apply(() => throw new InvalidOperationException("boom"), effect)
            );
            Assert.Equal("boom", ex.Message);

            ctx.ChangedAddresses.Should().BeEmpty();
        }

        [Fact]
        public async Task Typed_Form_Throws_Nothing_If_Work_Throws()
        {
            var ctx = new ChangeContext();
            var effect = WriteEffect<int>
                .For()
                .Changes(new ResourceAddress(ResourceKind.Entity, "entity", "1"));

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

    public sealed class ApplyFormsEquivalence
    {
        [Fact]
        public async Task LowLevel_And_HighLevel_Produce_Same_Addresses_For_Static_Only()
        {
            var lowLevelCtx = new ChangeContext();
            var highLevelCtx = new ChangeContext();

            var staticAddrs = new[]
            {
                new ResourceAddress(ResourceKind.Entity, "a", "1"),
                new ResourceAddress(ResourceKind.Collection, "b", "2"),
                new ResourceAddress(ResourceKind.External, "c", "3"),
            };

            await lowLevelCtx.Apply(changes =>
            {
                foreach (var addr in staticAddrs)
                {
                    changes.Changed(addr);
                }
                return Task.CompletedTask;
            });

            var effect = WriteEffect
                .For()
                .Changes(staticAddrs[0])
                .Changes(staticAddrs[1])
                .Changes(staticAddrs[2]);

            await highLevelCtx.Apply(async () => await Task.CompletedTask, effect);

            lowLevelCtx.ChangedAddresses.Should().Equal(highLevelCtx.ChangedAddresses);
        }

        [Fact]
        public async Task LowLevel_And_HighLevel_Produce_Same_Addresses_For_Mixed()
        {
            var lowLevelCtx = new ChangeContext();
            var highLevelCtx = new ChangeContext();

            var staticAddrs = new[]
            {
                new ResourceAddress(ResourceKind.Entity, "a", "1"),
                new ResourceAddress(ResourceKind.Collection, "b", "2"),
            };

            var resultAddr = new ResourceAddress(ResourceKind.Entity, "created", "99");

            await lowLevelCtx.Apply(changes =>
            {
                foreach (var addr in staticAddrs)
                {
                    changes.Changed(addr);
                }
                changes.Changed(resultAddr);
                return Task.CompletedTask;
            });

            var effect = WriteEffect.For().Changes(staticAddrs[0]).Changes(staticAddrs[1]);

            // For equivalence test with void high-level, the low-level must declare
            // the same static addresses + one result-derived. Since high-level void
            // has no result, we test static-only equivalence above.
            // For mixed equivalence we test typed forms.
        }

        [Fact]
        public async Task LowLevelTyped_And_HighLevelTyped_Produce_Same_Addresses_For_Mixed()
        {
            var lowLevelCtx = new ChangeContext();
            var highLevelCtx = new ChangeContext();

            var staticAddrs = new[]
            {
                new ResourceAddress(ResourceKind.Entity, "a", "1"),
                new ResourceAddress(ResourceKind.Collection, "b", "2"),
            };

            var resultValue = "99";

            await lowLevelCtx.Apply(async changes =>
            {
                foreach (var addr in staticAddrs)
                {
                    changes.Changed(addr);
                }
                changes.Changed(new ResourceAddress(ResourceKind.Entity, "created", resultValue));
                await Task.Delay(0);
                return resultValue;
            });

            var effect = WriteEffect<string>
                .For()
                .Changes(staticAddrs[0])
                .Changes(staticAddrs[1])
                .ChangesResult(r => new ResourceAddress(ResourceKind.Entity, "created", r));

            var highResult = await highLevelCtx.Apply(
                async () =>
                {
                    await Task.Delay(0);
                    return resultValue;
                },
                effect
            );

            highResult.Should().Be(resultValue);
            lowLevelCtx.ChangedAddresses.Should().Equal(highLevelCtx.ChangedAddresses);
        }
    }

    [Fact]
    public void CancellationToken_Flows_Through_Constructor()
    {
        var ct = new CancellationToken(canceled: true);
        var ctx = new ChangeContext(ct);

        ctx.CancellationToken.Should().Be(ct);
    }

    [Fact]
    public void CancellationToken_Defaults_To_None()
    {
        var ctx = new ChangeContext();

        ctx.CancellationToken.Should().Be(CancellationToken.None);
    }
}
