using Playground.Generated.Domain;
using Sluice;

namespace Playground.Generated.Application;

// The generator reads this interface and emits GeneratedDashboardResources plus
// GeneratedDashboardSluice. The generated wrapper owns TrackedRead/TrackedWrite.
[Sluice("GeneratedDashboard")]
public interface IGeneratedPlaygroundStore
{
    [ReadEntity("user")]
    public Task<User> GetUser(UserId id, CancellationToken ct);

    [ReadEntity("flag")]
    public Task<FeatureFlag> GetFlag(FeatureFlagId id, CancellationToken ct);

    [ReadEntity("greeting")]
    public Task<Greeting> GetGreeting(UserId id, CancellationToken ct);

    [WriteEntity("flag")]
    public Task ToggleFlag(FeatureFlagId id, CancellationToken ct);

    [WriteEntity("greeting")]
    public Task UpdateGreeting(UserId id, string text, CancellationToken ct);
}
