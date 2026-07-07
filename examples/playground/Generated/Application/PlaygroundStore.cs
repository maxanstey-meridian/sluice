using Playground.Generated.Domain;

namespace Playground.Generated.Application;

// Same demo data as the manual mode. This one implements the annotated
// interface so source generation can derive the Sluice wrapper.
public sealed class PlaygroundStore : IGeneratedPlaygroundStore
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

    public Task<User> GetUser(UserId id, CancellationToken ct) => Task.FromResult(_users[id]);

    public Task<FeatureFlag> GetFlag(FeatureFlagId id, CancellationToken ct) =>
        Task.FromResult(_flags[id]);

    public Task<Greeting> GetGreeting(UserId id, CancellationToken ct) =>
        Task.FromResult(_greetings[id]);

    // This mutates the shared dependency both dashboards read.
    public Task ToggleFlag(FeatureFlagId id, CancellationToken ct)
    {
        if (_flags.TryGetValue(id, out var flag))
        {
            _flags[id] = flag with { Enabled = !flag.Enabled };
        }

        return Task.CompletedTask;
    }

    // This mutates an admin-only dependency. Bob's dashboard never observes it.
    public Task UpdateGreeting(UserId id, string text, CancellationToken ct)
    {
        _greetings[id] = new Greeting(id, text);
        return Task.CompletedTask;
    }
}
