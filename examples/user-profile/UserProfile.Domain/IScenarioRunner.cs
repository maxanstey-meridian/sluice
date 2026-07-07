namespace UserProfile.Domain;

using Sluice;

public interface IScenarioRunner
{
    public InMemoryUserStore Store { get; }
    public ISluice Sluice { get; }

    public Task<User> GetUser(UserId id, CancellationToken ct);

    public Task<UserProfile> GetProfile(UserId id, CancellationToken ct);

    public Task<IReadOnlyList<User>> GetUsersByGroup(GroupId id, CancellationToken ct);

    public Task<Order> GetOrder(OrderId id, CancellationToken ct);

    public Task UpdateUser(UserId id, UpdateUserInput input, CancellationToken ct);

    public Task UpdateUsersByGroup(GroupId id, UpdateUserInput input, CancellationToken ct);

    public Task<User> CreateUser(UserId id, CreateUserInput input, CancellationToken ct);

    public Task UpdateOrder(OrderId id, CancellationToken ct);

    public Task InvalidateUser(UserId id, CancellationToken ct);
}
