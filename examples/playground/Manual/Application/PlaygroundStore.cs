using Playground.Manual.Domain;

namespace Playground.Manual.Application;

// Plain in-memory backing data for the demo.
// Nothing here knows about Sluice; reads and writes look like normal app code.
public sealed class PlaygroundStore
{
    private readonly Dictionary<string, User> _users = new()
    {
        ["alice"] = new User("alice", "Alice", "admin"),
        ["bob"] = new User("bob", "Bob", "member"),
    };

    private readonly Dictionary<string, FeatureFlag> _flags = new()
    {
        ["dark_mode"] = new FeatureFlag("dark_mode", true),
    };

    private readonly Dictionary<string, Greeting> _greetings = new()
    {
        ["alice"] = new Greeting("alice", "Welcome back, Admin!"),
    };

    public Task<User> GetUser(string id) => Task.FromResult(_users[id]);

    public Task<FeatureFlag> GetFlag(string id) => Task.FromResult(_flags[id]);

    public Task<Greeting> GetGreeting(string id) => Task.FromResult(_greetings[id]);

    // This mutates the shared dependency both dashboards read.
    public Task ToggleFlag(string id)
    {
        if (_flags.TryGetValue(id, out var flag))
        {
            _flags[id] = flag with { Enabled = !flag.Enabled };
        }

        return Task.CompletedTask;
    }

    // This mutates an admin-only dependency. Bob's dashboard never observes it.
    public Task UpdateGreeting(string id, string text)
    {
        _greetings[id] = new Greeting(id, text);
        return Task.CompletedTask;
    }
}
