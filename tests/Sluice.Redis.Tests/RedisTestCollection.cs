using Xunit;

namespace Sluice.Redis.Tests;

[CollectionDefinition(Name)]
public sealed class RedisTestCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
