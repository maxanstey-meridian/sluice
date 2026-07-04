namespace SluiceExample;

using Sluice;

public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record User(UserId Id, string Name, string Email);

public sealed record UserSettings(UserId Id, bool DarkMode, string Language);

public sealed record UserPreferences(UserId Id, string Theme);

public sealed record UserProfile(
    UserId Id,
    string Name,
    string Email,
    bool DarkMode,
    string Language,
    string Theme
);

public sealed record UpdateUserInput(string Name, bool DarkMode, string Theme);

public interface IUserStore
{
    public Task<User> GetUser(UserId id, CancellationToken ct);
    public Task<UserSettings> GetSettings(UserId id, CancellationToken ct);
    public Task<UserPreferences> GetPreferences(UserId id, CancellationToken ct);
    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct);
}

public sealed class InMemoryUserStore : IUserStore
{
    public int GetUserCallCount { get; private set; }
    public int GetSettingsCallCount { get; private set; }
    public int GetPreferencesCallCount { get; private set; }
    public int UpdateUserCallCount { get; private set; }

    private readonly Dictionary<UserId, User> _users = new()
    {
        [new UserId("alice")] = new User(new UserId("alice"), "Alice", "alice@example.com"),
        [new UserId("bob")] = new User(new UserId("bob"), "Bob", "bob@example.com"),
    };

    private readonly Dictionary<UserId, UserSettings> _settings = new()
    {
        [new UserId("alice")] = new UserSettings(new UserId("alice"), false, "en"),
        [new UserId("bob")] = new UserSettings(new UserId("bob"), true, "fr"),
    };

    private readonly Dictionary<UserId, UserPreferences> _preferences = new()
    {
        [new UserId("alice")] = new UserPreferences(new UserId("alice"), "system"),
        [new UserId("bob")] = new UserPreferences(new UserId("bob"), "compact"),
    };

    public Task<User> GetUser(UserId id, CancellationToken ct)
    {
        GetUserCallCount++;
        return Task.FromResult(_users[id]);
    }

    public Task<UserSettings> GetSettings(UserId id, CancellationToken ct)
    {
        GetSettingsCallCount++;
        return Task.FromResult(_settings[id]);
    }

    public Task<UserPreferences> GetPreferences(UserId id, CancellationToken ct)
    {
        GetPreferencesCallCount++;
        return Task.FromResult(_preferences[id]);
    }

    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct)
    {
        UpdateUserCallCount++;

        var user = _users[id];
        _users[id] = user with { Name = input.Name };

        var settings = _settings[id];
        _settings[id] = settings with { DarkMode = input.DarkMode };

        var preferences = _preferences[id];
        _preferences[id] = preferences with { Theme = input.Theme };

        return Task.CompletedTask;
    }
}
