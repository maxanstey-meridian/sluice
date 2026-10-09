using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Infrastructure;
using Sluice.Tests.Support;

namespace Sluice.Tests;

/// <summary>What a compute runs inside: no ambient transaction, and resources it holds until it ends.</summary>
public sealed class ComputeScopeTests
{
    private static readonly Source<int> _rows = new("scope-rows");
    private static readonly Query<string, bool> _query = new("scope-query");

    private sealed class Held : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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
        late.Should().Throw<InvalidOperationException>().WithMessage("*already returned*");
    }

    [Fact]
    public void Registering_Sluice_twice_throws()
    {
        var services = new ServiceCollection().AddSluice();

        var again = () => services.AddSluice();

        again
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*AddSluice was called more than once*");
    }
}
