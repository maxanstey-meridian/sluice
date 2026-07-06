namespace Sluice.Redis;

public sealed class RedisCircuitBreakerOptions
{
    public int FailureThreshold { get; init; } = 5;
    public TimeSpan CooldownDuration { get; init; } = TimeSpan.FromSeconds(10);
}
