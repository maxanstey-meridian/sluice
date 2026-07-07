using Playground.Generated.Domain;
using Sluice;

namespace Playground.Generated.Application;

// Generated mode keeps the same cached projection, but gets tracked reads and
// writes from the source-generated GeneratedDashboardSluice wrapper.
public sealed class DashboardCache
{
    private readonly SluiceKernel _sluice;
    private readonly PlaygroundStore _store;
    private readonly GeneratedDashboardSluice _dashboardSluice;
    private readonly CachedQuery<UserId, Dashboard> _dashboardQuery;

    public DashboardCache(SluiceKernel sluice, PlaygroundStore store)
    {
        _sluice = sluice;
        _store = store;
        _dashboardSluice = new GeneratedDashboardSluice(_sluice, _store);

        // The query is still hand-written. Codegen replaces the resource wrapper,
        // not the application projection.
        _dashboardQuery = new CachedQuery<UserId, Dashboard>(
            "dashboard",
            ComputeDashboard,
            ttl: TimeSpan.FromMinutes(5)
        );
    }

    private async ValueTask<Dashboard> ComputeDashboard(UserId id, IReadScope scope)
    {
        var user = await _dashboardSluice.User.Get(id, scope);
        var flag = await _dashboardSluice.Flag.Get(FeatureFlagId.DarkMode, scope);
        Greeting? greeting = null;

        if (user.Role == "admin")
        {
            greeting = await _dashboardSluice.Greeting.Get(id, scope);
        }

        return new Dashboard(user, flag, greeting);
    }

    public Task<Dashboard> GetDashboard(UserId user, CancellationToken ct) =>
        _sluice.Get(_dashboardQuery, user, ct);

    public async Task<FeatureFlag> ToggleDarkMode(CancellationToken ct)
    {
        await _dashboardSluice.ToggleFlag(FeatureFlagId.DarkMode, ct);
        return await _store.GetFlag(FeatureFlagId.DarkMode, ct);
    }

    public Task UpdateGreeting(UserId user, string text, CancellationToken ct) =>
        _dashboardSluice.UpdateGreeting(user, text, ct);

    public Task FlushAll(CancellationToken ct) => _sluice.FlushAllAsync(ct);
}
