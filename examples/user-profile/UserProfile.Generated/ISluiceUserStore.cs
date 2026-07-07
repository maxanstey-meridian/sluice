namespace UserProfile.Generated;

using Sluice;
using UserProfile.Domain;

[Sluice]
public interface ISluiceUserStore
{
    [ReadEntity("user")]
    public Task<User> GetUser(UserId id, CancellationToken ct);

    [ReadEntity("userSettings")]
    public Task<UserSettings> GetSettings(UserId id, CancellationToken ct);

    [ReadEntity("userPreferences")]
    public Task<UserPreferences> GetPreferences(UserId id, CancellationToken ct);

    [ReadCollection("users", "byGroup")]
    public Task<IReadOnlyList<User>> GetUsersByGroup(GroupId id, CancellationToken ct);

    [ReadEntity("order")]
    public Task<Order> GetOrder(OrderId id, CancellationToken ct);

    [WriteEntity("user")]
    [WriteEntity("userSettings")]
    [WriteEntity("userPreferences")]
    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct);

    [WriteCollection("users", "byGroup")]
    public Task UpdateUsersByGroup(GroupId id, UpdateUserInput input, CancellationToken ct);

    [WriteEntity("user", ResultKey = nameof(User.Id))]
    public Task<User> CreateUser(UserId id, CreateUserInput input, CancellationToken ct);

    [WriteEntity("order")]
    public Task UpdateOrder(OrderId id, CancellationToken ct);
}
