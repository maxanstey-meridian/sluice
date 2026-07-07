using Playground.Generated.Domain;

namespace Playground.Generated.Application;

// Same demo data as the manual mode. This one implements the annotated
// interface so source generation can derive the Sluice wrapper.
public sealed class PlaygroundStore : IGeneratedPlaygroundStore
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

    public Task<User> GetUser(StringKey id, CancellationToken ct) =>
        Task.FromResult(_users[id.Value]);

    public Task<FeatureFlag> GetFlag(StringKey id, CancellationToken ct) =>
        Task.FromResult(_flags[id.Value]);

    public Task<Greeting> GetGreeting(StringKey id, CancellationToken ct) =>
        Task.FromResult(_greetings[id.Value]);

    // This mutates the shared dependency both dashboards read.
    public Task ToggleFlag(StringKey id, CancellationToken ct)
    {
        if (_flags.TryGetValue(id.Value, out var flag))
        {
            _flags[id.Value] = flag with { Enabled = !flag.Enabled };
        }

        return Task.CompletedTask;
    }

    // This mutates an admin-only dependency. Bob's dashboard never observes it.
    public Task UpdateGreeting(StringKey id, string text, CancellationToken ct)
    {
        _greetings[id.Value] = new Greeting(id.Value, text);
        return Task.CompletedTask;
    }
}
