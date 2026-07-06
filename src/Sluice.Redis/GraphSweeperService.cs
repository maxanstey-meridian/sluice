using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sluice.Redis;

public sealed class GraphSweeperService(
    RedisGraphSweeper sweeper,
    GraphSweeperOptions options,
    ILogger<GraphSweeperService>? logger = null
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var removed = await sweeper.SweepAsync(options.BatchSize, ct);
                if (removed > 0)
                {
                    logger?.LogInformation(
                        "Graph sweeper removed {Count} orphaned edge(s)",
                        removed
                    );
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Graph sweeper error");
            }

            await Task.Delay(options.Interval, ct);
        }
    }
}
