using System.Transactions;
using Sluice.Infrastructure;
using Sluice.Tests.Support;

namespace Sluice.Tests;

/// <summary>What a compute runs inside: no ambient transaction, and resources it holds until it ends.</summary>
public sealed class ComputeScopeTests
{
    private static readonly Source<int> _rows = new("scope-rows");
    private static readonly Query<string, bool> _query = new("scope-query");

    private class Held : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Exception? Throws { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return Throws is null ? ValueTask.CompletedTask : ValueTask.FromException(Throws);
        }
    }

    // A compute holds one instance per type, so these let it hold three.
    private sealed class Second : Held;

    private sealed class Third : Held;

    // Equal to every other one, so only a by-reference check tells them apart.
    private sealed class Lookalike : IAsyncDisposable
    {
        public override bool Equals(object? obj) => obj is Lookalike;

        public override int GetHashCode() => 0;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_compute_runs_outside_the_caller_s_ambient_transaction_and_the_caller_gets_it_back()
    {
        await using var node = new SluiceNode();
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var caller = Transaction.Current;

        var inside = await node.Cache.GetOrSetAsync(
            _query,
            "ambient",
            (read, _) =>
            {
                read.From(_rows.For(1));
                return Task.FromResult(Transaction.Current is null);
            }
        );

        inside.Should().BeTrue();
        Transaction.Current.Should().BeSameAs(caller);
    }

    [Fact]
    public async Task What_a_compute_holds_is_one_instance_per_type_and_is_disposed_when_it_ends()
    {
        await using var node = new SluiceNode();
        Held? first = null;
        Held? second = null;
        var heldInside = false;
        SluiceReads? escaped = null;

        await node.Cache.GetOrSetAsync(
            _query,
            "held",
            (read, _) =>
            {
                read.From(_rows.For(1));
                escaped = read;
                first = ActiveCompute.Hold(read, _ => new Held());
                second = ActiveCompute.Hold(read, _ => new Held());
                heldInside = ActiveCompute.Holds(read, first) && !first.Disposed;
                return Task.FromResult(true);
            }
        );

        second.Should().BeSameAs(first);
        heldInside.Should().BeTrue();
        first!.Disposed.Should().BeTrue();
        var late = () => ActiveCompute.Hold(escaped!, _ => new Held());
        late.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*already returned, so it can't open anything more*");
    }

    [Fact]
    public async Task A_compute_holds_only_the_very_instance_it_was_given()
    {
        await using var node = new SluiceNode();
        var holdsItsOwn = false;
        var holdsALookalike = true;

        await node.Cache.GetOrSetAsync(
            _query,
            "lookalike",
            (read, _) =>
            {
                read.From(_rows.For(1));
                var own = ActiveCompute.Hold(read, _ => new Lookalike());
                holdsItsOwn = ActiveCompute.Holds(read, own);
                holdsALookalike = ActiveCompute.Holds(read, new Lookalike());
                return Task.FromResult(true);
            }
        );

        holdsItsOwn.Should().BeTrue();
        holdsALookalike.Should().BeFalse();
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("is cancelled")]
    [InlineData("records a violation")]
    [InlineData("records no dependencies")]
    public async Task What_a_compute_holds_is_disposed_however_the_compute_ends(string ending)
    {
        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        Held? held = null;

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                ending,
                (read, ct) =>
                {
                    held = ActiveCompute.Hold(read, _ => new Held());
                    if (ending != "records no dependencies")
                    {
                        read.From(_rows.For(1));
                    }

                    switch (ending)
                    {
                        case "throws":
                            throw new InvalidOperationException("compute failed");
                        case "is cancelled":
                            caller.Cancel();
                            ct.ThrowIfCancellationRequested();
                            break;
                        case "records a violation":
                            // Swallowed by the compute; the call still throws it.
                            _ = ActiveCompute.Violate(
                                read,
                                new InvalidOperationException("guard violated")
                            );
                            break;
                    }

                    return Task.FromResult(true);
                },
                caller.Token
            );

        if (ending == "is cancelled")
        {
            await call.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await call.Should().ThrowAsync<InvalidOperationException>();
        }

        held!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task When_one_held_resource_s_dispose_throws_the_rest_are_still_disposed_and_its_exception_reaches_the_caller()
    {
        await using var node = new SluiceNode();
        var failure = new InvalidOperationException("dispose failed");
        var held = new List<Held>();

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "one-dispose-throws",
                (read, _) =>
                {
                    read.From(_rows.For(1));
                    held.Add(ActiveCompute.Hold(read, _ => new Held()));
                    held.Add(ActiveCompute.Hold(read, _ => new Second { Throws = failure }));
                    held.Add(ActiveCompute.Hold(read, _ => new Third()));
                    return Task.FromResult(true);
                }
            );

        (await call.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should()
            .BeSameAs(failure);
        held.Should().HaveCount(3).And.OnlyContain(resource => resource.Disposed);
    }

    [Fact]
    public async Task When_several_held_resources_dispose_throws_all_are_disposed_and_the_caller_gets_every_exception()
    {
        await using var node = new SluiceNode();
        var first = new InvalidOperationException("first dispose failed");
        var second = new InvalidOperationException("second dispose failed");
        var held = new List<Held>();

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "several-disposes-throw",
                (read, _) =>
                {
                    read.From(_rows.For(1));
                    held.Add(ActiveCompute.Hold(read, _ => new Held { Throws = first }));
                    held.Add(ActiveCompute.Hold(read, _ => new Second()));
                    held.Add(ActiveCompute.Hold(read, _ => new Third { Throws = second }));
                    return Task.FromResult(true);
                }
            );

        (await call.Should().ThrowAsync<AggregateException>())
            .Which.InnerExceptions.Should()
            .BeEquivalentTo([first, second]);
        held.Should().HaveCount(3).And.OnlyContain(resource => resource.Disposed);
    }
}
