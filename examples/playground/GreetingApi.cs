using System.Collections.Concurrent;

namespace Playground;

// Stands in for a remote greeting API, so the playground has a data source that isn't EF. Sluice can't see
// its writes, so they invalidate by hand.
public sealed class GreetingApi
{
    private readonly ConcurrentDictionary<UserId, string> _greetings = new()
    {
        [new UserId("alice")] = "Hello, Admin!",
    };

    public async Task<string?> GetAsync(UserId userId, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20), ct); // a network round trip
        return _greetings.GetValueOrDefault(userId);
    }

    public Task SetAsync(UserId userId, string text)
    {
        _greetings[userId] = text;
        return Task.CompletedTask;
    }
}
