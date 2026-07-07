using Playground.Manual.Domain;

namespace Playground.Manual.Application;

// Plain in-memory backing data for the demo.
// Nothing here knows about Sluice; reads and writes look like normal app code.
public sealed class PlaygroundStore
{
    private readonly Dictionary<UserId, User> _users = new()
    {
        ["alice"] = new User("alice", "Alice", "admin"),
        ["bob"] = new User("bob", "Bob", "member"),
    };

    private readonly Dictionary<FeatureFlagId, FeatureFlag> _flags = new()
    {
        [FeatureFlagId.DarkMode] = new FeatureFlag(FeatureFlagId.DarkMode, true),
    };

    private readonly Dictionary<UserId, Greeting> _greetings = new()
    {
        ["alice"] = new Greeting("alice", "Welcome back, Admin!"),
    };

    public Task<User> GetUser(UserId id) => Task.FromResult(_users[id]);

    public Task<FeatureFlag> GetFlag(FeatureFlagId id) => Task.FromResult(_flags[id]);

    public Task<Greeting> GetGreeting(UserId id) => Task.FromResult(_greetings[id]);

    // This mutates the shared dependency both dashboards read.
    public Task ToggleFlag(FeatureFlagId id)
    {
        if (_flags.TryGetValue(id, out var flag))
        {
            _flags[id] = flag with { Enabled = !flag.Enabled };
        }

        return Task.CompletedTask;
    }

    // This mutates an admin-only dependency. Bob's dashboard never observes it.
    public Task UpdateGreeting(UserId id, string text)
    {
        _greetings[id] = new Greeting(id, text);
        return Task.CompletedTask;
    }
}
