namespace UserProfile.Domain;

public sealed class InMemoryUserStore : IUserStore
{
    public int GetUserCallCount { get; private set; }
    public int GetSettingsCallCount { get; private set; }
    public int GetPreferencesCallCount { get; private set; }
    public int GetUsersByGroupCallCount { get; private set; }
    public int GetOrderCallCount { get; private set; }
    public int UpdateUserCallCount { get; private set; }
    public int UpdateUsersByGroupCallCount { get; private set; }
    public int CreateUserCallCount { get; private set; }
    public int UpdateOrderCallCount { get; private set; }

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

    private readonly Dictionary<GroupId, List<UserId>> _groups = new()
    {
        [new GroupId("admins")] = [new UserId("alice")],
        [new GroupId("users")] = [new UserId("alice"), new UserId("bob")],
    };

    private readonly Dictionary<OrderId, Order> _orders = new()
    {
        [new OrderId("order-1")] = new Order(new OrderId("order-1"), new UserId("alice"), 99.95m),
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

    public Task<IReadOnlyList<User>> GetUsersByGroup(GroupId id, CancellationToken ct)
    {
        GetUsersByGroupCallCount++;
        if (!_groups.TryGetValue(id, out var userIds))
        {
            return Task.FromResult<IReadOnlyList<User>>(Array.Empty<User>());
        }

        return Task.FromResult<IReadOnlyList<User>>(
            userIds.Select(uid => _users[uid]).OrderBy(u => u.Id.Value).ToArray()
        );
    }

    public Task<Order> GetOrder(OrderId id, CancellationToken ct)
    {
        GetOrderCallCount++;
        return Task.FromResult(_orders[id]);
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

    public Task UpdateUsersByGroup(GroupId id, UpdateUserInput input, CancellationToken ct)
    {
        UpdateUsersByGroupCallCount++;

        if (_groups.TryGetValue(id, out var userIds))
        {
            foreach (var uid in userIds)
            {
                if (_users.TryGetValue(uid, out var user))
                {
                    _users[uid] = user with { Name = input.Name };
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<User> CreateUser(UserId id, CreateUserInput input, CancellationToken ct)
    {
        CreateUserCallCount++;

        var user = new User(id, input.Name, input.Email);
        _users[id] = user;
        _settings[id] = new UserSettings(id, false, "en");
        _preferences[id] = new UserPreferences(id, "system");

        return Task.FromResult(user);
    }

    public Task UpdateOrder(OrderId id, CancellationToken ct)
    {
        UpdateOrderCallCount++;

        if (_orders.TryGetValue(id, out var order))
        {
            _orders[id] = order with { Total = order.Total + 1m };
        }

        return Task.CompletedTask;
    }
}
