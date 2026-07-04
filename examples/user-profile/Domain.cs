namespace SluiceExample;

using Sluice;

public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record User(UserId Id, string Name, string Email);

public sealed record UserSettings(UserId Id, bool DarkMode, string Language);

public sealed record UserProfile(UserId Id, string Name, string Email, bool DarkMode, string Language);

public interface IUserStore
{
    public Task<User> GetUser(UserId id);
    public Task<UserSettings> GetSettings(UserId id);
    public Task UpdateUserName(UserId id, string name);
    public Task UpdateDarkMode(UserId id, bool darkMode);
}

public sealed class InMemoryUserStore : IUserStore
{
    public int GetUserCallCount { get; private set; }
    public int GetSettingsCallCount { get; private set; }
    public int UpdateUserNameCallCount { get; private set; }
    public int UpdateDarkModeCallCount { get; private set; }

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

    public Task<User> GetUser(UserId id)
    {
        GetUserCallCount++;
        return Task.FromResult(_users[id]);
    }

    public Task<UserSettings> GetSettings(UserId id)
    {
        GetSettingsCallCount++;
        return Task.FromResult(_settings[id]);
    }

    public Task UpdateUserName(UserId id, string name)
    {
        UpdateUserNameCallCount++;
        var existing = _users[id];
        _users[id] = existing with { Name = name };
        return Task.CompletedTask;
    }

    public Task UpdateDarkMode(UserId id, bool darkMode)
    {
        UpdateDarkModeCallCount++;
        var existing = _settings[id];
        _settings[id] = existing with { DarkMode = darkMode };
        return Task.CompletedTask;
    }
}
