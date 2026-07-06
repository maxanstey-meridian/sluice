namespace Sluice.Redis;

public enum BreakerState
{
    Closed,
    Open,
    Probing,
}

public sealed class RedisCircuitBreaker(
    RedisCircuitBreakerOptions options,
    TimeProvider? clock = null
)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _failureCount;
    private int _state = (int)BreakerState.Closed;
    private long _openedAtTicks;
    private int _probing;

    public BreakerState State => (BreakerState)Volatile.Read(ref _state);

    public bool TryAllowCall()
    {
        var state = Volatile.Read(ref _state);

        switch (state)
        {
            case (int)BreakerState.Closed:
                return true;
            case (int)BreakerState.Probing:
            {
                var probing = Interlocked.CompareExchange(ref _probing, 1, 0);
                return probing == 0;
            }
        }

        var openedAt = Volatile.Read(ref _openedAtTicks);
        var elapsed = _clock.GetUtcNow() - new DateTimeOffset(openedAt, TimeSpan.Zero);
        if (elapsed < options.CooldownDuration)
        {
            return false;
        }

        var current = Interlocked.CompareExchange(
            ref _state,
            (int)BreakerState.Probing,
            (int)BreakerState.Open
        );
        if (current != (int)BreakerState.Open)
        {
            return false;
        }
        var probe = Interlocked.CompareExchange(ref _probing, 1, 0);
        return probe == 0;
    }

    public void RecordSuccess()
    {
        for (; ; )
        {
            var state = Volatile.Read(ref _state);
            switch (state)
            {
                case (int)BreakerState.Probing:
                {
                    var current = Interlocked.CompareExchange(
                        ref _state,
                        (int)BreakerState.Closed,
                        (int)BreakerState.Probing
                    );
                    if (current == (int)BreakerState.Probing)
                    {
                        Interlocked.Exchange(ref _failureCount, 0);
                        Interlocked.Exchange(ref _probing, 0);
                        return;
                    }
                    continue;
                }
                case (int)BreakerState.Closed:
                    Interlocked.Exchange(ref _failureCount, 0);
                    return;
                default:
                    return;
            }
        }
    }

    public void RecordFailure()
    {
        var state = Volatile.Read(ref _state);
        switch (state)
        {
            case (int)BreakerState.Probing:
                Interlocked.Exchange(ref _probing, 0);
                Volatile.Write(ref _openedAtTicks, _clock.GetUtcNow().Ticks);
                Interlocked.Exchange(ref _state, (int)BreakerState.Open);
                return;
            case (int)BreakerState.Closed:
            {
                var count = Interlocked.Increment(ref _failureCount);
                if (count >= options.FailureThreshold)
                {
                    Volatile.Write(ref _openedAtTicks, _clock.GetUtcNow().Ticks);
                    Interlocked.Exchange(ref _state, (int)BreakerState.Open);
                }

                break;
            }
        }
    }
}
