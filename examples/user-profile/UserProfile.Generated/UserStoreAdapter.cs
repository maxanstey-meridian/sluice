namespace UserProfile.Generated;

using UserProfile.Domain;

internal sealed class UserStoreAdapter(IUserStore inner) : ISluiceUserStore
{
    public Task<User> GetUser(UserId id, CancellationToken ct) => inner.GetUser(id, ct);

    public Task<UserSettings> GetSettings(UserId id, CancellationToken ct) =>
        inner.GetSettings(id, ct);

    public Task<UserPreferences> GetPreferences(UserId id, CancellationToken ct) =>
        inner.GetPreferences(id, ct);

    public Task<IReadOnlyList<User>> GetUsersByGroup(GroupId id, CancellationToken ct) =>
        inner.GetUsersByGroup(id, ct);

    public Task<Order> GetOrder(OrderId id, CancellationToken ct) => inner.GetOrder(id, ct);

    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct) =>
        inner.UpdateUser(id, input, ct);

    public Task UpdateUsersByGroup(GroupId id, UpdateUserInput input, CancellationToken ct) =>
        inner.UpdateUsersByGroup(id, input, ct);

    public Task<User> CreateUser(UserId id, CreateUserInput input, CancellationToken ct) =>
        inner.CreateUser(id, input, ct);

    public Task UpdateOrder(OrderId id, CancellationToken ct) => inner.UpdateOrder(id, ct);
}
