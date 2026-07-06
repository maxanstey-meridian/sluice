using FluentAssertions;
using Xunit;

namespace Sluice.Redis.Tests;

public sealed class RedisCircuitBreakerTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset FakeNow { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => FakeNow;
    }

    private static RedisCircuitBreaker CreateBreaker(
        FakeTimeProvider timeProvider,
        int failureThreshold = 5,
        TimeSpan? cooldown = null
    )
    {
        var options = new RedisCircuitBreakerOptions
        {
            FailureThreshold = failureThreshold,
            CooldownDuration = cooldown ?? TimeSpan.FromSeconds(10),
        };
        return new RedisCircuitBreaker(options, timeProvider);
    }

    [Fact]
    public void InitialState_IsClosed()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time);
        breaker.State.Should().Be(BreakerState.Closed);
    }

    [Fact]
    public void TryAllowCall_ReturnsTrue_WhenClosed()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time);
        breaker.TryAllowCall().Should().BeTrue();
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5);

        for (int i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }
        breaker.State.Should().Be(BreakerState.Closed);
        breaker.RecordSuccess();
        breaker.State.Should().Be(BreakerState.Closed);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.State.Should().Be(BreakerState.Closed);
    }

    [Fact]
    public void ConsecutiveFailures_TransitionsToOpen()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5);

        for (int i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }
        breaker.State.Should().Be(BreakerState.Closed);

        breaker.RecordFailure();
        breaker.State.Should().Be(BreakerState.Open);
    }

    [Fact]
    public void State_Open_TryAllowCall_ReturnsFalse_BeforeCooldown()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5, cooldown: TimeSpan.FromSeconds(10));

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        breaker.State.Should().Be(BreakerState.Open);
        breaker.TryAllowCall().Should().BeFalse();
    }

    [Fact]
    public void State_Open_TryAllowCall_TransitionsToProbing_AfterCooldown()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5, cooldown: TimeSpan.FromSeconds(10));

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        breaker.State.Should().Be(BreakerState.Open);

        time.FakeNow = DateTimeOffset.FromUnixTimeSeconds(10);
        breaker.TryAllowCall().Should().BeTrue();
        breaker.State.Should().Be(BreakerState.Probing);
    }

    [Fact]
    public void Probe_Success_TransitionsToClosed()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5, cooldown: TimeSpan.FromSeconds(10));

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        time.FakeNow = DateTimeOffset.FromUnixTimeSeconds(10);
        breaker.TryAllowCall().Should().BeTrue();
        breaker.State.Should().Be(BreakerState.Probing);

        breaker.RecordSuccess();
        breaker.State.Should().Be(BreakerState.Closed);
    }

    [Fact]
    public void Probe_Failure_TransitionsToOpen()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5, cooldown: TimeSpan.FromSeconds(10));

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        time.FakeNow = DateTimeOffset.FromUnixTimeSeconds(10);
        breaker.TryAllowCall().Should().BeTrue();
        breaker.State.Should().Be(BreakerState.Probing);

        breaker.RecordFailure();
        breaker.State.Should().Be(BreakerState.Open);
    }

    [Fact]
    public async Task ConcurrentProbes_PreventsMultipleProbes()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 5, cooldown: TimeSpan.FromSeconds(10));

        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        time.FakeNow = DateTimeOffset.FromUnixTimeSeconds(10);

        var results = new bool[10];
        var startSignal = new TaskCompletionSource();
        var tasks = Enumerable
            .Range(0, 10)
            .Select(async i =>
            {
                await startSignal.Task;
                results[i] = breaker.TryAllowCall();
            })
            .ToArray();

        startSignal.SetResult();

        await Task.WhenAll(tasks);

        var allowed = results.Count(r => r);
        allowed.Should().BeGreaterOrEqualTo(1);
        allowed.Should().BeLessOrEqualTo(2);
    }

    [Fact]
    public void Open_Closed_Open_Transitions_Reopen()
    {
        var time = new FakeTimeProvider();
        var breaker = CreateBreaker(time, failureThreshold: 3, cooldown: TimeSpan.FromSeconds(1));

        for (int i = 0; i < 3; i++)
        {
            breaker.RecordFailure();
        }
        breaker.State.Should().Be(BreakerState.Open);

        time.FakeNow = DateTimeOffset.FromUnixTimeSeconds(1);
        breaker.TryAllowCall().Should().BeTrue();
        breaker.State.Should().Be(BreakerState.Probing);

        breaker.RecordFailure();
        breaker.State.Should().Be(BreakerState.Open);

        time.FakeNow = DateTimeOffset.FromUnixTimeSeconds(2);
        breaker.TryAllowCall().Should().BeTrue();
        breaker.State.Should().Be(BreakerState.Probing);

        breaker.RecordSuccess();
        breaker.State.Should().Be(BreakerState.Closed);
    }
}
