namespace Sluice;

public sealed class StampedeOptions
{
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
}
