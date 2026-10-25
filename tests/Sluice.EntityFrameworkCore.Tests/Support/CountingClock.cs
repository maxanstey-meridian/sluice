using Microsoft.Extensions.Time.Testing;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that counts the recurring timers made on it and not yet disposed: the EF package's
/// sweep of joined transactions is the only one, since the core's re-publish timers fire once. Each timer runs on the
/// flow it was made on, as the system's do.
/// </summary>
public sealed class CountingClock : TimeProvider
{
    private readonly FakeTimeProvider _inner = new();
    private int _recurring;

    public int LiveRecurringTimers => Volatile.Read(ref _recurring);

    public void Advance(TimeSpan by) => _inner.Advance(by);

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        // As System.Threading.Timer does; FakeTimeProvider's own run on the flow of whatever advances the clock.
        var flow = ExecutionContext.Capture();
        var timer = _inner.CreateTimer(
            flow is null
                ? callback
                : value => ExecutionContext.Run(flow, new ContextCallback(callback), value),
            state,
            dueTime,
            period
        );
        if (period == Timeout.InfiniteTimeSpan)
        {
            return timer;
        }

        Interlocked.Increment(ref _recurring);
        return new Counted(timer, this);
    }

    private sealed class Counted(ITimer inner, CountingClock clock) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref clock._recurring);
            }

            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
