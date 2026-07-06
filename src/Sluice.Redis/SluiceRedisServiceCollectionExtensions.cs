using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Sluice.Redis;

public static class SluiceRedisServiceCollectionExtensions
{
    public static IServiceCollection AddSluiceRedisGraphSweeper(
        this IServiceCollection services,
        Action<GraphSweeperOptions>? configure = null
    )
    {
        var options = new GraphSweeperOptions();
        configure?.Invoke(options);

        services.AddHostedService(sp =>
        {
            var redis = sp.GetRequiredService<IConnectionMultiplexer>();
            var graphStore = sp.GetRequiredService<IGraphStore>();
            var logger = sp.GetService<ILogger<GraphSweeperService>>();
            var sweeper = new RedisGraphSweeper(redis, graphStore, options.KeyPrefix);
            return new GraphSweeperService(sweeper, options, logger);
        });

        return services;
    }
}
