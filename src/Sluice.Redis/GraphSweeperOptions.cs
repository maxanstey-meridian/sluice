namespace Sluice.Redis;

public sealed class GraphSweeperOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; set; } = 500;
    public string KeyPrefix { get; set; } = "sluice";
}
