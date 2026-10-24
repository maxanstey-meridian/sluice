using System.Transactions;
using Sluice.Infrastructure;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

/// <summary>
/// What a compute runs inside: no ambient transaction, resources it holds until it ends, and steps it runs before its
/// value is stored.
/// </summary>
public sealed class ComputeScopeTests
{
    private static readonly Source<int> _rows = new("scope-rows");
    private static readonly Query<string, bool> _query = new("scope-query");
    private static readonly Query<string, object?> _checked = new("scope-checked");

    private class Held : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Exception? Throws { get; init; }

        public List<Held>? Log { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Log?.Add(this);
            return Throws is null ? ValueTask.CompletedTask : ValueTask.FromException(Throws);
        }
    }

    // A compute holds one instance per type, so these let it hold three.
    private sealed class Second : Held;

    private sealed class Third : Held;

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
                heldInside = ActiveCompute.Held<Held>(read) == first && !first.Disposed;
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

    // One made later may use one made earlier, as a context uses its connection.
    [Fact]
    public async Task What_a_compute_holds_is_disposed_newest_first()
    {
        await using var node = new SluiceNode();
        var disposed = new List<Held>();
        var held = new List<Held>();

        await node.Cache.GetOrSetAsync(
            _query,
            "order",
            (read, _) =>
            {
                read.From(_rows.For(1));
                held.Add(ActiveCompute.Hold(read, _ => new Held { Log = disposed }));
                held.Add(ActiveCompute.Hold(read, _ => new Second { Log = disposed }));
                held.Add(ActiveCompute.Hold(read, _ => new Third { Log = disposed }));
                return Task.FromResult(true);
            }
        );

        disposed.Should().Equal(held[2], held[1], held[0]);
    }

    [Fact]
    public async Task Held_returns_what_the_compute_holds_and_makes_nothing()
    {
        await using var node = new SluiceNode();
        Held? before = new();
        Held? own = null;
        Held? after = null;
        Second? other = new();

        await node.Cache.GetOrSetAsync(
            _query,
            "peek",
            (read, _) =>
            {
                read.From(_rows.For(1));
                before = ActiveCompute.Held<Held>(read);
                own = ActiveCompute.Hold(read, _ => new Held());
                after = ActiveCompute.Held<Held>(read);
                other = ActiveCompute.Held<Second>(read);
                return Task.FromResult(true);
            }
        );

        before.Should().BeNull();
        after.Should().BeSameAs(own);
        other.Should().BeNull("Held makes nothing");
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

        switch (ending)
        {
            case "is cancelled":
                await call.Should().ThrowAsync<OperationCanceledException>();
                break;
            case "throws":
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("compute failed");
                break;
            case "records a violation":
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("guard violated");
                break;
            default:
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("*recorded no dependencies*");
                break;
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
                    return Task.FromResult(false);
                }
            );

        (await call.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should()
            .BeSameAs(failure);
        held.Should().HaveCount(3).And.OnlyContain(resource => resource.Disposed);
        (await node.Cache.GetOrSetAsync(_query, "one-dispose-throws", Fresh)).Should().BeTrue();
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

    [Fact]
    public async Task When_the_compute_throws_and_a_dispose_throws_the_caller_gets_both_and_nothing_is_cached()
    {
        await using var node = new SluiceNode();
        var computeFailure = new InvalidOperationException("compute failed");
        var disposeFailure = new InvalidOperationException("dispose failed");

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "compute-and-dispose-throw",
                (read, _) =>
                {
                    read.From(_rows.For(1));
                    ActiveCompute.Hold(read, _ => new Held { Throws = disposeFailure });
                    return Task.FromException<bool>(computeFailure);
                }
            );

        (await call.Should().ThrowAsync<AggregateException>())
            .Which.InnerExceptions.Should()
            .Equal(computeFailure, disposeFailure);
        (await node.Cache.GetOrSetAsync(_query, "compute-and-dispose-throw", Fresh))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task When_the_compute_records_a_violation_and_a_dispose_throws_the_caller_gets_the_violation()
    {
        await using var node = new SluiceNode();
        var violation = new InvalidOperationException("guard violated");

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "violation-and-dispose-throw",
                (read, ct) =>
                {
                    read.From(_rows.For(1));
                    ActiveCompute.Hold(
                        read,
                        _ => new Held { Throws = new InvalidOperationException("dispose failed") }
                    );
                    _ = ActiveCompute.Violate(read, violation);
                    return Task.FromResult(false);
                }
            );

        (await call.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should()
            .BeSameAs(violation);
        (await node.Cache.GetOrSetAsync(_query, "violation-and-dispose-throw", Fresh))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task When_a_cancelled_compute_s_dispose_throws_the_caller_gets_both()
    {
        await using var node = new SluiceNode();
        var disposeFailure = new InvalidOperationException("dispose failed");
        using var cancel = new CancellationTokenSource();

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "cancelled-and-dispose-throws",
                async (read, ct) =>
                {
                    read.From(_rows.For(1));
                    ActiveCompute.Hold(read, _ => new Held { Throws = disposeFailure });
                    await cancel.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                    return false;
                },
                cancel.Token
            );

        var thrown = (await call.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().HaveCount(2);
        thrown.InnerExceptions[0].Should().BeAssignableTo<OperationCanceledException>();
        thrown.InnerExceptions[1].Should().BeSameAs(disposeFailure);
    }

    // A dispose that threw out of the factory would let fail-safe serve the expired value, whether or not the
    // compute recorded dependencies (without them the no-dependencies guard would stop the store anyway).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dispose_that_throws_stores_nothing_even_when_fail_safe_has_an_expired_entry(
        bool records
    )
    {
        await using var node = new SluiceNode(cache =>
            cache.WithDefaultEntryOptions(o => o.IsFailSafeEnabled = true)
        );
        var disposeFailure = new InvalidOperationException("dispose failed");
        await node.Cache.GetOrSetAsync(_query, "fail-safe", Fresh);
        await node.Fusion.ExpireAsync("scope-query:fail-safe");

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "fail-safe",
                (read, _) =>
                {
                    if (records)
                    {
                        read.From(_rows.For(1));
                    }

                    ActiveCompute.Hold(read, _ => new Held { Throws = disposeFailure });
                    return Task.FromResult(false);
                }
            );

        (await call.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should()
            .BeSameAs(disposeFailure);
        (await node.Cache.GetOrSetAsync(_query, "fail-safe", Fresh)).Should().BeTrue();
    }

    [Fact]
    public async Task Steps_run_in_order_after_the_compute_returns_and_before_its_value_is_stored()
    {
        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        var log = new List<string>();
        var runs = 0;

        Task<bool> Get() =>
            node
                .Cache.GetOrSetAsync(
                    _query,
                    "steps",
                    (read, _) =>
                    {
                        runs++;
                        read.From(_rows.For(1));
                        var held = ActiveCompute.Hold(read, _ => new Held());
                        ActiveCompute.BeforeStore(
                            read,
                            ct =>
                            {
                                log.Add(
                                    $"first: own compute {ActiveCompute.Reads == read}, held {!held.Disposed}, "
                                        + $"caller's token {ct == caller.Token}"
                                );
                                read.From(_rows.For(2));
                                return Task.CompletedTask;
                            }
                        );
                        ActiveCompute.BeforeStore(
                            read,
                            async _ =>
                            {
                                await Task.Yield();
                                log.Add("second");
                            }
                        );
                        log.Add("compute returns");
                        return Task.FromResult(true);
                    },
                    caller.Token
                )
                .AsTask();

        await Get();

        log.Should()
            .Equal(
                "compute returns",
                "first: own compute True, held True, caller's token True",
                "second"
            );
        await node.Cache.InvalidateAsync(_rows.For(2));
        await Get();
        runs.Should().Be(2, "the step's read was recorded with the value");
    }

    // Steps run inside the compute's ambient-suppressing scope, so a step's query reads what's committed too.
    [Fact]
    public async Task A_step_runs_outside_the_caller_s_ambient_transaction()
    {
        await using var node = new SluiceNode();
        var ran = false;
        Transaction? seen = null;
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

        await node.Cache.GetOrSetAsync(
            _query,
            "step-suppressed",
            (read, _) =>
            {
                read.From(_rows.For(1));
                ActiveCompute.BeforeStore(
                    read,
                    _ =>
                    {
                        ran = true;
                        seen = Transaction.Current;
                        return Task.CompletedTask;
                    }
                );
                return Task.FromResult(true);
            }
        );

        ran.Should().BeTrue();
        seen.Should().BeNull();
        Transaction.Current.Should().NotBeNull("the caller has its transaction back");
    }

    [Fact]
    public async Task A_step_added_by_a_step_runs_too()
    {
        await using var node = new SluiceNode();
        var log = new List<string>();

        await node.Cache.GetOrSetAsync(
            _query,
            "step-adds-step",
            (read, _) =>
            {
                read.From(_rows.For(1));
                ActiveCompute.BeforeStore(
                    read,
                    _ =>
                    {
                        log.Add("first");
                        ActiveCompute.BeforeStore(
                            read,
                            _ =>
                            {
                                log.Add("added by first");
                                return Task.CompletedTask;
                            }
                        );
                        return Task.CompletedTask;
                    }
                );
                ActiveCompute.BeforeStore(
                    read,
                    _ =>
                    {
                        log.Add("second");
                        return Task.CompletedTask;
                    }
                );
                return Task.FromResult(true);
            }
        );

        log.Should().Equal("first", "second", "added by first");
    }

    [Fact]
    public async Task A_step_that_throws_fails_the_call_and_caches_nothing()
    {
        await using var node = new SluiceNode();
        var failure = new InvalidOperationException("step failed");
        Held? held = null;

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                "step-throws",
                (read, _) =>
                {
                    read.From(_rows.For(1));
                    held = ActiveCompute.Hold(read, _ => new Held());
                    ActiveCompute.BeforeStore(read, _ => Task.FromException(failure));
                    return Task.FromResult(false);
                }
            );

        (await call.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should()
            .BeSameAs(failure);
        held!.Disposed.Should().BeTrue();
        (await node.Cache.GetOrSetAsync(_query, "step-throws", Fresh)).Should().BeTrue();
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("records a violation")]
    [InlineData("leaves uncached")]
    public async Task Steps_do_not_run_when_nothing_will_be_stored(string ending)
    {
        await using var node = new SluiceNode();
        var ran = false;

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                ending,
                (read, ct) =>
                {
                    read.From(_rows.For(1));
                    ActiveCompute.BeforeStore(
                        read,
                        _ =>
                        {
                            ran = true;
                            return Task.CompletedTask;
                        }
                    );
                    switch (ending)
                    {
                        case "throws":
                            throw new InvalidOperationException("compute failed");
                        case "records a violation":
                            _ = ActiveCompute.Violate(
                                read,
                                new InvalidOperationException("guard violated")
                            );
                            break;
                        default:
                            read.DoNotCache();
                            break;
                    }

                    return Task.FromResult(false);
                }
            );

        if (ending == "leaves uncached")
        {
            (await call()).Should().BeFalse();
        }
        else
        {
            await call.Should().ThrowAsync<InvalidOperationException>();
        }

        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Adding_a_step_after_the_compute_returned_throws()
    {
        await using var node = new SluiceNode();
        SluiceReads? escaped = null;

        await node.Cache.GetOrSetAsync(
            _query,
            "late-step",
            (read, _) =>
            {
                read.From(_rows.For(1));
                escaped = read;
                return Task.FromResult(true);
            }
        );

        var late = () => ActiveCompute.BeforeStore(escaped!, _ => Task.CompletedTask);
        late.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*already returned, so nothing more can run before its value is stored*");
    }

    [Fact]
    public async Task A_store_check_that_refuses_a_value_fails_the_call_and_caches_nothing()
    {
        var refused = new object();
        var refusal = new InvalidOperationException("refused");
        var log = new List<string>();
        SluiceReads? running = null;
        Held? held = null;
        var runs = 0;

        Exception? Check(object value)
        {
            log.Add(
                $"{(value == refused ? "refused" : "kept")}: own compute {ActiveCompute.Reads == running}, "
                    + $"held {held is { Disposed: false }}"
            );
            return value == refused ? refusal : null;
        }

        await using var node = new SluiceNode(services: services =>
        {
            StoreChecks.Add(services, Check);
            StoreChecks.Add(services, Check);
        });

        Task<object?> Get(string key, object? value) =>
            node
                .Cache.GetOrSetAsync(
                    _checked,
                    key,
                    (read, _) =>
                    {
                        runs++;
                        running = read;
                        read.From(_rows.For(1));
                        held = ActiveCompute.Hold(read, _ => new Held());
                        return Task.FromResult(value);
                    }
                )
                .AsTask();

        var call = () => Get("k", refused);

        (await call.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should()
            .BeSameAs(refusal);
        held!.Disposed.Should().BeTrue();
        var kept = new object();
        (await Get("k", kept)).Should().BeSameAs(kept, "nothing was cached");
        (await Get("k", refused)).Should().BeSameAs(kept, "a hit isn't checked");
        (await Get("null", null)).Should().BeNull();
        runs.Should().Be(3);
        log.Should()
            .Equal("refused: own compute True, held True", "kept: own compute True, held True");
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("records a violation")]
    [InlineData("records no dependencies")]
    [InlineData("leaves uncached")]
    [InlineData("runs while the flow has uncommitted writes")]
    public async Task Store_checks_do_not_run_when_nothing_will_be_stored(string ending)
    {
        var ran = false;
        await using var node = new SluiceNode(services: services =>
            StoreChecks.Add(
                services,
                _ =>
                {
                    ran = true;
                    return new InvalidOperationException("refused");
                }
            )
        );
        using var flow = SluiceFlow.Begin();
        if (ending == "runs while the flow has uncommitted writes")
        {
            FlowWrites.AddProbe(node.Cache, "transaction", () => [_rows.For(2)]);
        }

        var call = async () =>
            await node.Cache.GetOrSetAsync(
                _query,
                ending,
                (read, ct) =>
                {
                    if (ending != "records no dependencies")
                    {
                        read.From(_rows.For(1));
                    }

                    switch (ending)
                    {
                        case "throws":
                            throw new InvalidOperationException("compute failed");
                        case "records a violation":
                            _ = ActiveCompute.Violate(
                                read,
                                new InvalidOperationException("guard violated")
                            );
                            break;
                        case "leaves uncached":
                            read.DoNotCache();
                            break;
                    }

                    return Task.FromResult(false);
                }
            );

        switch (ending)
        {
            case "throws":
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("compute failed");
                break;
            case "records a violation":
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("guard violated");
                break;
            case "records no dependencies":
                await call.Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("*recorded no dependencies*");
                break;
            default:
                (await call()).Should().BeFalse();
                break;
        }

        ran.Should().BeFalse();
    }

    private static Task<bool> Fresh(SluiceReads read, CancellationToken ct)
    {
        read.From(_rows.For(1));
        return Task.FromResult(true);
    }
}
