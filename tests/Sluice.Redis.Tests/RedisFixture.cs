using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Sluice.Redis.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;
    private ConnectionMultiplexer? _redis;

    public ConnectionMultiplexer Redis =>
        _redis ?? throw new InvalidOperationException("Redis fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        _container = new RedisBuilder("redis:7").Build();
        await _container.StartAsync();
        _redis = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public void FlushDatabase()
    {
        Redis.GetDatabase().Execute("FLUSHDB");
    }
}
