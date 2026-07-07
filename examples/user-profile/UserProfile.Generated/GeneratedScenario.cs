namespace UserProfile.Generated;

using Sluice;
using UserProfile.Domain;

public sealed class GeneratedScenario : IScenarioRunner
{
    private readonly InMemoryUserStore _store = new();
    private readonly SluiceKernel _sluice;
    private readonly SluiceUserStoreSluice _userSluice;

    private readonly CachedQuery<UserId, User> _userById;
    private readonly CachedQuery<UserId, UserProfile> _profile;
    private readonly CachedQuery<GroupId, IReadOnlyList<User>> _usersByGroup;
    private readonly CachedQuery<OrderId, Order> _orderById;

    public GeneratedScenario(IEventSink? eventSink = null)
    {
        _sluice = new SluiceKernel(new InMemoryCacheStore(), eventSink: eventSink);
        _userSluice = new SluiceUserStoreSluice(_sluice, new UserStoreAdapter(_store));

        _userById = new CachedQuery<UserId, User>(
            "user.byId",
            id => id.Value,
            async (id, scope) => await _userSluice.User.Get(id, scope)
        );

        _profile = new CachedQuery<UserId, UserProfile>(
            "user.profile",
            id => id.Value,
            async (id, scope) =>
            {
                var u = await _userSluice.User.Get(id, scope);
                var s = await _userSluice.Settings.Get(id, scope);
                var p = await _userSluice.Preferences.Get(id, scope);
                return new UserProfile(id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
            }
        );

        _usersByGroup = new CachedQuery<GroupId, IReadOnlyList<User>>(
            "users.byGroup",
            id => id.Value,
            async (id, scope) => await _userSluice.UsersByGroup.Get(id, scope)
        );

        _orderById = new CachedQuery<OrderId, Order>(
            "order.byId",
            id => id.Value,
            async (id, scope) => await _userSluice.Order.Get(id, scope)
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
        _userSluice.UpdateUser(id, input, ct);

    public Task UpdateUsersByGroup(GroupId id, UpdateUserInput input, CancellationToken ct) =>
        _userSluice.UpdateUsersByGroup(id, input, ct);

    public Task<User> CreateUser(UserId id, CreateUserInput input, CancellationToken ct) =>
        _userSluice.CreateUser(id, input, ct);

    public Task UpdateOrder(OrderId id, CancellationToken ct) => _userSluice.UpdateOrder(id, ct);

    public Task InvalidateUser(UserId id, CancellationToken ct) =>
        _sluice.Invalidate(new WriteEffect(SluiceUserStoreResources.User.For(id)), ct);
}
