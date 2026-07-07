using Playground.Generated.Domain;
using Sluice;

namespace Playground.Generated.Application;

// The generator reads this interface and emits GeneratedDashboardResources plus
// GeneratedDashboardSluice. The generated wrapper owns TrackedRead/TrackedWrite.
[Sluice("GeneratedDashboard")]
public interface IGeneratedPlaygroundStore
{
    [ReadEntity("user")]
    public Task<User> GetUser(StringKey id, CancellationToken ct);

    [ReadEntity("flag")]
    public Task<FeatureFlag> GetFlag(StringKey id, CancellationToken ct);

    [ReadEntity("greeting")]
    public Task<Greeting> GetGreeting(StringKey id, CancellationToken ct);

    [WriteEntity("flag")]
    public Task ToggleFlag(StringKey id, CancellationToken ct);

    [WriteEntity("greeting")]
    public Task UpdateGreeting(StringKey id, string text, CancellationToken ct);
}
