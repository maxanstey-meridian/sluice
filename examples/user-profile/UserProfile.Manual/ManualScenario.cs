namespace UserProfile.Manual;

using Sluice;
using UserProfile.Domain;

public sealed class ManualScenario : IScenarioRunner
{
    private readonly InMemoryUserStore _store = new();
    private readonly SluiceKernel _sluice;

    private readonly TrackedRead<UserId, User> _userRead;
    private readonly TrackedRead<UserId, UserSettings> _settingsRead;
    private readonly TrackedRead<UserId, UserPreferences> _preferencesRead;
    private readonly TrackedRead<GroupId, IReadOnlyList<User>> _usersByGroupRead;
    private readonly TrackedRead<OrderId, Order> _orderRead;

    private readonly TrackedWrite<UserId> _updateUser;
    private readonly TrackedWrite<GroupId> _updateUsersByGroup;
    private readonly TrackedWrite<UserId, User> _createUser;
    private readonly TrackedWrite<OrderId> _updateOrder;

    private readonly CachedQuery<UserId, User> _userById;
    private readonly CachedQuery<UserId, UserProfile> _profile;
    private readonly CachedQuery<GroupId, IReadOnlyList<User>> _usersByGroup;
    private readonly CachedQuery<OrderId, Order> _orderById;

    public ManualScenario(IEventSink? eventSink = null)
    {
        _sluice = new SluiceKernel(new InMemoryCacheStore(), eventSink: eventSink);

        _userRead = ManualResources.User.Read(_store.GetUser);
        _settingsRead = ManualResources.UserSettings.Read(_store.GetSettings);
        _preferencesRead = ManualResources.UserPreferences.Read(_store.GetPreferences);
        _usersByGroupRead = ManualResources.UsersByGroup.Read(_store.GetUsersByGroup);
        _orderRead = ManualResources.Order.Read(_store.GetOrder);

        _updateUser = new TrackedWrite<UserId>(
            _sluice,
            ManualResources.User.For,
            ManualResources.UserSettings.For,
            ManualResources.UserPreferences.For
        );

        _updateUsersByGroup = new TrackedWrite<GroupId>(_sluice, ManualResources.UsersByGroup.For);

        _createUser = new TrackedWrite<UserId, User>(
            _sluice,
            [],
            u => ManualResources.User.For(u.Id)
        );

        _updateOrder = new TrackedWrite<OrderId>(_sluice, ManualResources.Order.For);

        _userById = new CachedQuery<UserId, User>(
            "user.byId",
            id => id.Value,
            async (id, scope) => await _userRead.Get(id, scope)
        );

        _profile = new CachedQuery<UserId, UserProfile>(
            "user.profile",
            id => id.Value,
            async (id, scope) =>
            {
                var u = await _userRead.Get(id, scope);
                var s = await _settingsRead.Get(id, scope);
                var p = await _preferencesRead.Get(id, scope);
                return new UserProfile(id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
            }
        );

        _usersByGroup = new CachedQuery<GroupId, IReadOnlyList<User>>(
            "users.byGroup",
            id => id.Value,
            async (id, scope) => await _usersByGroupRead.Get(id, scope)
        );

        _orderById = new CachedQuery<OrderId, Order>(
            "order.byId",
            id => id.Value,
            async (id, scope) => await _orderRead.Get(id, scope)
        );
    }

    public InMemoryUserStore Store => _store;
    public ISluice Sluice => _sluice;

    public Task<User> GetUser(UserId id, CancellationToken ct) => _sluice.Get(_userById, id, ct);

    public Task<UserProfile> GetProfile(UserId id, CancellationToken ct) =>
        _sluice.Get(_profile, id, ct);

    public Task<IReadOnlyList<User>> GetUsersByGroup(GroupId id, CancellationToken ct) =>
        _sluice.Get(_usersByGroup, id, ct);

    public Task<Order> GetOrder(OrderId id, CancellationToken ct) =>
        _sluice.Get(_orderById, id, ct);

    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct) =>
        _updateUser.Write(id, ct => _store.UpdateUser(id, input, ct), ct);

    public Task UpdateUsersByGroup(GroupId id, UpdateUserInput input, CancellationToken ct) =>
        _updateUsersByGroup.Write(id, ct => _store.UpdateUsersByGroup(id, input, ct), ct);

    public Task<User> CreateUser(UserId id, CreateUserInput input, CancellationToken ct) =>
        _createUser.Write(id, ct => _store.CreateUser(id, input, ct), ct);

    public Task UpdateOrder(OrderId id, CancellationToken ct) =>
        _updateOrder.Write(id, ct => _store.UpdateOrder(id, ct), ct);

    public Task InvalidateUser(UserId id, CancellationToken ct) =>
        _sluice.Invalidate(new WriteEffect(ManualResources.User.For(id)), ct);
}
